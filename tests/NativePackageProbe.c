#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

// A native host avoids accidentally borrowing the test runner's installed .NET runtime.
int wmain(int argc, wchar_t **argv)
{
    if (argc != 2) return 2;
    wchar_t directory[MAX_PATH], dllPath[MAX_PATH], runtimePath[MAX_PATH];
    if (!GetFullPathNameW(argv[1], MAX_PATH, directory, NULL)) return 3;
    swprintf_s(dllPath, MAX_PATH, L"%s\\NPPAI.dll", directory);
    SetEnvironmentVariableW(L"DOTNET_ROOT", L"Z:\\NPPAI-no-global-runtime");
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", L"Z:\\NPPAI-no-global-runtime");
    SetEnvironmentVariableW(L"DOTNET_MULTILEVEL_LOOKUP", L"0");
    SetDllDirectoryW(directory);
    HMODULE plugin = LoadLibraryW(dllPath);
    if (!plugin) { fwprintf(stderr, L"Plugin load failed: %lu\n", GetLastError()); return 4; }
    const wchar_t *(__cdecl *getName)(void) = (const wchar_t *(__cdecl *)(void))GetProcAddress(plugin, "getName");
    if (!getName || wcscmp(getName(), L"NPPAI") != 0) return 5;
    if (!GetModuleFileNameW(GetModuleHandleW(L"coreclr.dll"), runtimePath, MAX_PATH)) return 6;
    size_t length = wcslen(directory);
    if (_wcsnicmp(runtimePath, directory, length) || runtimePath[length] != L'\\') {
        fwprintf(stderr, L"Used an external runtime: %s\n", runtimePath); return 7;
    }
    wprintf(L"PASS native plugin exported NPPAI using bundled runtime: %s\n", runtimePath);
    return 0;
}
