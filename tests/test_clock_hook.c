/* Isolated test host: does not touch Explorer or the taskbar. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

typedef struct ClockOptions {
    DWORD size, controller_pid, top_is_china, reserved;
    WCHAR local_label[32], china_label[32];
    WCHAR time_format[64], date_format[64], top_template[96], bottom_template[96];
    uintptr_t test_module, test_time_return, test_date_return;
} ClockOptions;
typedef DWORD (WINAPI *HookFn)(void *);

__declspec(noinline) int test_clock_time(WCHAR *buffer, int capacity) {
    volatile int n = GetTimeFormatEx(NULL, 0, NULL, L"HH':'mm", buffer, capacity);
    return n;
}
__declspec(noinline) int test_clock_date(WCHAR *buffer, int capacity) {
    volatile int n = GetDateFormatEx(NULL, 0, NULL, L"yyyy'/'MM'/'dd", buffer, capacity, NULL);
    return n;
}
__declspec(noinline) int test_unrelated_time(WCHAR *buffer, int capacity) {
    volatile int n = GetTimeFormatEx(NULL, 0, NULL, L"HH':'mm", buffer, capacity);
    return n;
}

static void *find_import(HMODULE module, const char *name) {
    BYTE *base = (BYTE *)module;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    IMAGE_IMPORT_DESCRIPTOR *desc = (IMAGE_IMPORT_DESCRIPTOR *)(base + nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    for (; desc->Name; ++desc) {
        if (!desc->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA64 *names = (IMAGE_THUNK_DATA64 *)(base + desc->OriginalFirstThunk);
        IMAGE_THUNK_DATA64 *slots = (IMAGE_THUNK_DATA64 *)(base + desc->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME *by_name = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
            if (!strcmp((char *)by_name->Name, name)) return &slots->u1.Function;
        }
    }
    return NULL;
}

static uintptr_t find_return(void *function, void *slot) {
    BYTE *code = (BYTE *)function;
    for (int i = 0; i < 120; ++i) {
        if (code[i] != 0xff || code[i + 1] != 0x15) continue;
        int32_t displacement;
        memcpy(&displacement, code + i + 2, sizeof(displacement));
        if ((void *)(code + i + 6 + displacement) == slot) return (uintptr_t)(code + i + 6);
    }
    return 0;
}

int main(void) {
    HMODULE self = GetModuleHandleW(NULL);
    HMODULE hook = LoadLibraryW(L"clock_hook.test.dll");
    if (!hook) { printf("FAIL: cannot load hook dll (%lu)\n", GetLastError()); return 1; }
    HookFn install = (HookFn)GetProcAddress(hook, "InstallClockHook");
    HookFn uninstall = (HookFn)GetProcAddress(hook, "UninstallClockHook");
    ClockOptions o = {0};
    o.size = sizeof(o); o.controller_pid = GetCurrentProcessId();
    wcscpy(o.local_label, L"USA"); wcscpy(o.china_label, L"CHINA");
    wcscpy(o.time_format, L"HH:mm"); wcscpy(o.date_format, L"yyyy/MM/dd");
    wcscpy(o.top_template, L"%label% %time%"); wcscpy(o.bottom_template, L"%label% %time%");
    o.test_module = (uintptr_t)self;
    o.test_time_return = find_return(test_clock_time, find_import(self, "GetTimeFormatEx"));
    o.test_date_return = find_return(test_clock_date, find_import(self, "GetDateFormatEx"));
    if (!install || !uninstall || !o.test_time_return || !o.test_date_return) {
        printf("FAIL: missing exports or test call sites\n"); return 1;
    }
    WCHAR baseline[128], untouched[128], display_time[128], display_date[128], small[2] = { L'!', 0 };
    if (!test_clock_time(baseline, 128)) { printf("FAIL: baseline\n"); return 1; }
    DWORD status = install(&o);
    if (status) { printf("FAIL: install status %lu\n", status); return 1; }
    int need_time = test_clock_time(NULL, 0);
    int written_time = test_clock_time(display_time, 128);
    int need_date = test_clock_date(NULL, 0);
    int written_date = test_clock_date(display_date, 128);
    if (wcsncmp(display_time, L"USA ", 4) || wcsncmp(display_date, L"CHINA ", 6) ||
        need_time != written_time || need_date != written_date ||
        need_time != (int)wcslen(display_time) + 1 || need_date != (int)wcslen(display_date) + 1) {
        printf("FAIL: formatted text or buffer length\n"); return 1;
    }
    SetLastError(0);
    if (test_clock_time(small, 2) != 0 || GetLastError() != ERROR_INSUFFICIENT_BUFFER || small[0] != L'!') {
        printf("FAIL: insufficient buffer handling\n"); return 1;
    }
    if (!test_unrelated_time(untouched, 128) || wcscmp(baseline, untouched)) {
        printf("FAIL: unrelated caller changed\n"); return 1;
    }
    if (uninstall(NULL) || !test_clock_time(untouched, 128) || wcscmp(baseline, untouched)) {
        printf("FAIL: restoration\n"); return 1;
    }
    o.top_is_china = 1;
    wcscpy(o.bottom_template, L"%label% %date% %time%");
    wcscpy(o.time_format, L"h:mm tt");
    if (install(&o) || !test_clock_time(display_time, 128) || wcsncmp(display_time, L"CHINA ", 6) ||
        !test_clock_date(display_date, 128) || wcsncmp(display_date, L"USA ", 4) ||
        !wcschr(display_date, L'/') || uninstall(NULL)) {
        printf("FAIL: reversed timezones, 12-hour format, date template or repeat restoration\n"); return 1;
    }
    printf("PASS: templates, reversed zones, 12-hour time, dates, buffers, unaffected caller, restoration\n");
    return 0;
}
