define(['emby-button', 'emby-input'], function () {
    'use strict';
    return function (view) {
        var state = null, busy = false, disposed = false;
        function element(name) { return view.querySelector('[data-update="' + name + '"]'); }
        function request(route, body) {
            var options = { type: body === undefined ? 'GET' : 'POST', url: ApiClient.getUrl('BlurayHelper/PluginUpdate' + route), dataType: 'json' };
            if (body !== undefined) { options.contentType = 'application/json'; options.data = JSON.stringify(body); }
            return ApiClient.ajax(options);
        }
        function controls() {
            view.querySelectorAll('button, input').forEach(function (button) { button.disabled = busy; });
            element('install').disabled = busy || !state || !state.canInstall;
        }
        function render(result) {
            if (disposed) return;
            state = result;
            element('loaded').textContent = result.loadedVersion;
            element('installed').textContent = result.installedVersion;
            element('available').textContent = result.availableVersion || '尚未检查';
            element('source').value = result.sourceUrl;
            element('status').textContent = result.message;
            element('status').style.color = result.restartRequired ? '#e5a444' : '';
            element('checked').textContent = result.checkedAt ? '上次检查：' + new Date(result.checkedAt).toLocaleString() : '';
            controls();
        }
        function errorMessage(error) {
            if (error && error.responseText) {
                try { var body = JSON.parse(error.responseText); return body.Message || body.message || body.error || error.responseText; } catch (_) { }
            }
            return error && error.message || '请求失败。请检查 Helper 地址、服务状态及 Emby 日志。';
        }
        async function action(task, message) {
            if (busy || disposed) return;
            busy = true; controls(); element('error').hidden = true;
            if (message) element('status').textContent = message;
            try { render(await task()); }
            catch (error) {
                if (!disposed) {
                    if (state) state.canInstall = false;
                    element('error').textContent = errorMessage(error); element('error').hidden = false;
                    element('status').textContent = '操作未完成，请查看下方错误信息。';
                }
            } finally { busy = false; if (!disposed) controls(); }
        }
        function refresh() { return action(function () { return request(''); }); }
        function check(event) {
            event.preventDefault();
            return action(async function () {
                await request('/Source', { HelperUrl: element('source').value.trim() });
                return request('/Check', {});
            }, '正在检查 Helper 提供的插件版本…');
        }
        function install() {
            if (!state || !state.canInstall) return;
            return action(function () { return request('/Install', { Version: state.availableVersion, Sha256: state.sha256 }); }, '正在下载、校验并安装更新，请稍候…');
        }
        function show() { disposed = false; refresh(); }
        function destroy() {
            disposed = true;
            element('form').removeEventListener('submit', check);
            element('install').removeEventListener('click', install);
            element('refresh').removeEventListener('click', refresh);
            view.removeEventListener('viewshow', show);
            view.removeEventListener('viewdestroy', destroy);
        }
        element('form').addEventListener('submit', check);
        element('install').addEventListener('click', install);
        element('refresh').addEventListener('click', refresh);
        view.addEventListener('viewshow', show);
        view.addEventListener('viewdestroy', destroy);
    };
});
