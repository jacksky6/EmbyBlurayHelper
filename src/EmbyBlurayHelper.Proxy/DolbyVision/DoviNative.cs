using System.Runtime.InteropServices;

namespace EmbyBlurayHelper.Proxy.DolbyVision;

// libdovi 3.4 C ABI. No subprocess per frame and no decoded video buffers.
internal sealed class DoviNative : IDisposable
{
    private readonly IntPtr library;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Parse(byte[] bytes, nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Get(IntPtr rpu);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ConvertMode(IntPtr rpu, byte mode);
    [StructLayout(LayoutKind.Sequential)] private struct Data { internal IntPtr Pointer; internal nuint Length; }
    private readonly Parse parse;
    private readonly Get error, header, write;
    private readonly Free freeRpu, freeHeader, freeData;
    private readonly ConvertMode convert;
    internal DoviNative(string path)
    {
        library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            parse = Bind<Parse>("dovi_parse_unspec62_nalu"); error = Bind<Get>("dovi_rpu_get_error");
            header = Bind<Get>("dovi_rpu_get_header"); write = Bind<Get>("dovi_write_unspec62_nalu");
            freeRpu = Bind<Free>("dovi_rpu_free"); freeHeader = Bind<Free>("dovi_rpu_free_header"); freeData = Bind<Free>("dovi_data_free");
            convert = Bind<ConvertMode>("dovi_convert_rpu_with_mode");
        }
        catch { NativeLibrary.Free(library); throw; }
    }
    internal byte[] ToP81(ReadOnlySpan<byte> nal)
    {
        if (nal.Length is < 3 or > 65536 || (nal[0] >> 1 & 63) != 62) throw new InvalidDataException("Invalid Dolby Vision RPU NAL.");
        var bytes = nal.ToArray(); var rpu = parse(bytes, (nuint)bytes.Length);
        if (rpu == IntPtr.Zero) throw new InvalidDataException("libdovi could not allocate an RPU.");
        try
        {
            Check(rpu); var h = header(rpu);
            if (h == IntPtr.Zero) throw new InvalidDataException("Missing Dolby Vision RPU header.");
            byte profile; try { profile = Marshal.ReadByte(h); } finally { freeHeader(h); }
            if (profile != 7) throw new InvalidDataException($"Expected actual Profile 7 RPU, got {profile}.");
            if (convert(rpu, 2) != 0) { Check(rpu); throw new InvalidDataException("libdovi conversion failed."); }
            var output = write(rpu); Check(rpu);
            if (output == IntPtr.Zero) throw new InvalidDataException("libdovi produced no RPU.");
            try
            {
                var data = Marshal.PtrToStructure<Data>(output);
                if (data.Pointer == IntPtr.Zero || data.Length is < 3 or > 65536) throw new InvalidDataException("Invalid converted RPU length.");
                var result = new byte[(int)data.Length]; Marshal.Copy(data.Pointer, result, 0, result.Length); return result;
            }
            finally { freeData(output); }
        }
        finally { freeRpu(rpu); }
    }
    private void Check(IntPtr rpu)
    { var message = error(rpu); if (message != IntPtr.Zero) throw new InvalidDataException("libdovi: " + Marshal.PtrToStringUTF8(message)); }
    public void Dispose() => NativeLibrary.Free(library);
}
