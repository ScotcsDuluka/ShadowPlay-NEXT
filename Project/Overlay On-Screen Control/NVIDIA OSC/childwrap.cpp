// childwrap — จับ exit code ของ child จริง (bisect renderer WAS_KILLED 2026-10-07)
// CEF ชี้ subprocess_path มาที่ wrapper · wrapper spawn exe จริง (env OSC_REAL_EXE)
// แล้วจด exit code + arg แรกลง Logs\child-exit.log
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <shellapi.h>
#include <cstdio>
#include <string>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int n = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &n);
    wchar_t real[MAX_PATH] = L"";
    GetEnvironmentVariableW(L"OSC_REAL_EXE", real, MAX_PATH);
    if (!real[0]) return 9;
    std::wstring cmd = L"\"";
    cmd += real; cmd += L"\"";
    for (int i = 1; i < n; i++) { cmd += L" \""; cmd += argv[i]; cmd += L"\""; }
    STARTUPINFOW si{ sizeof(si) };
    PROCESS_INFORMATION pi{};
    // redirect stderr/stdout ของ child → child-stderr.log (บันทึกเหตุผลตายของ renderer)
    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, TRUE };
    HANDLE logf = CreateFileW(L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\child-stderr.log",
        FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_ALWAYS, 0, nullptr);
    if (logf != INVALID_HANDLE_VALUE) {
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdInput = nullptr;
        si.hStdOutput = logf;
        si.hStdError = logf;
    }
    BOOL ok = CreateProcessW(nullptr, &cmd[0], nullptr, nullptr, TRUE,
        CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &si, &pi);
    if (logf != INVALID_HANDLE_VALUE) CloseHandle(logf);
    if (!ok) {
        FILE* f = nullptr; _wfopen_s(&f, L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\child-exit.log", L"a");
        if (f) { fwprintf(f, L"SPAWN-FAIL err=%lu\r\n", GetLastError()); fclose(f); }
        return 8;
    }
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1; GetExitCodeProcess(pi.hProcess, &code);
    FILE* f = nullptr; _wfopen_s(&f, L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\child-exit.log", L"a");
    if (f) {
        wchar_t tag[80] = L"none";
        for (int i = 1; i < n; i++) {
            wchar_t* t = wcsstr(argv[i], L"--type=");
            if (t) { _snwprintf_s(tag, _TRUNCATE, L"%s", t + 7); break; }
        }
        wchar_t line[160];
        _snwprintf_s(line, _TRUNCATE, L"type=%s exit=%lu\r\n", tag, code);
        fwprintf(f, L"%s", line); fclose(f);
    }
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    return (int)code;
}
