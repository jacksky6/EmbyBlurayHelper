using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;

namespace EmbyBlurayHelper.EmbyPlugin.Thumbnails;

// Reuse the native extractor in full, overriding only its protected input-
// argument extension method. M2TS seeks can otherwise yield damaged open-GOP
// frames with missing references. Keyframe-only decoding needs no long preroll.
// This creates a separate subclass; it does not patch any Emby type or method.
internal static class NativeKeyframeExtractorFactory
{
    private static readonly ConcurrentDictionary<Type, Type> Types = new();
    internal static object Create(Type parent, IServerApplicationHost host) =>
        Activator.CreateInstance(Types.GetOrAdd(parent, Build), host)!;

    private static Type Build(Type parent)
    {
        var constructor = parent.GetConstructor(new[] { typeof(IServerApplicationHost) });
        var method = parent.GetMethod("GetExtraInputArguments", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(MediaStream) }, null);
        if (!parent.IsVisible || parent.IsSealed || constructor is null || method is null || !method.IsVirtual || method.IsFinal ||
            !method.IsFamily || method.ReturnType != typeof(string))
            throw new NotSupportedException("此 Emby 截图引擎未提供兼容的关键帧输入扩展，暂不提取原盘截图，避免损坏画面。");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("EmbyBlurayHelper.NativeImage." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Images").DefineType("DiscKeyframeImageExtractor", TypeAttributes.Public | TypeAttributes.Sealed, parent);
        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IServerApplicationHost) });
        var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, constructor); il.Emit(OpCodes.Ret);
        var extra = type.DefineMethod(method.Name, MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(string), new[] { typeof(string), typeof(MediaStream) });
        il = extra.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, method); il.Emit(OpCodes.Ldstr, " -skip_frame nokey");
        il.Emit(OpCodes.Call, typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!); il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(extra, method);
        return type.CreateTypeInfo()!.AsType();
    }
}
