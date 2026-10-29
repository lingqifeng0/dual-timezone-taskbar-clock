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
    WCHAR local_zone_id[128], target_zone_id[128];
    uintptr_t test_module, test_time_return, test_date_return, test_local_return;
} ClockOptions;
typedef DWORD (WINAPI *HookFn)(void *);
typedef struct TestTimezoneArgs { WCHAR zone_id[128]; SYSTEMTIME utc, local; } TestTimezoneArgs;

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
__declspec(noinline) void test_clock_schedule(SYSTEMTIME *value) {
    GetLocalTime(value);
    volatile WORD keep_call = value->wSecond; (void)keep_call;
}
__declspec(noinline) void test_other_local(SYSTEMTIME *value) {
    GetLocalTime(value);
    volatile WORD keep_call = value->wSecond; (void)keep_call;
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
    HookFn convert = (HookFn)GetProcAddress(hook, "TestTimezoneConversion");
    TestTimezoneArgs tz = {0};
    wcscpy(tz.zone_id, L"Eastern Standard Time");
    tz.utc.wYear = 2026; tz.utc.wMonth = 3; tz.utc.wDay = 8; tz.utc.wHour = 6; tz.utc.wMinute = 59;
    if (!convert || convert(&tz) || tz.local.wHour != 1 || tz.local.wMinute != 59) {
        printf("FAIL: US eastern time before spring DST\n"); return 1;
    }
    tz.utc.wHour = 7; tz.utc.wMinute = 0;
    if (convert(&tz) || tz.local.wHour != 3 || tz.local.wMinute != 0) {
        printf("FAIL: US eastern time after spring DST\n"); return 1;
    }
    wcscpy(tz.zone_id, L"Tokyo Standard Time");
    tz.utc.wHour = 23; tz.utc.wMinute = 30;
    if (convert(&tz) || tz.local.wDay != 9 || tz.local.wHour != 8 || tz.local.wMinute != 30) {
        printf("FAIL: Tokyo time and date rollover\n"); return 1;
    }
    ClockOptions o = {0};
    o.size = sizeof(o); o.controller_pid = GetCurrentProcessId();
    wcscpy(o.local_label, L"USA"); wcscpy(o.china_label, L"CHINA");
    wcscpy(o.time_format, L"HH:mm"); wcscpy(o.date_format, L"yyyy/MM/dd");
    wcscpy(o.top_template, L"%label% %time%"); wcscpy(o.bottom_template, L"%label% %time%");
    wcscpy(o.target_zone_id, L"China Standard Time");
    o.test_module = (uintptr_t)self;
    o.test_time_return = find_return(test_clock_time, find_import(self, "GetTimeFormatEx"));
    o.test_date_return = find_return(test_clock_date, find_import(self, "GetDateFormatEx"));
    o.test_local_return = find_return(test_clock_schedule, find_import(self, "GetLocalTime"));
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
    wcscpy(o.local_zone_id, L"Eastern Standard Time"); wcscpy(o.target_zone_id, L"Tokyo Standard Time");
    wcscpy(o.time_format, L"HH:mm"); o.top_is_china = 0;
    wcscpy(o.top_template, L"%label% %time%"); wcscpy(o.bottom_template, L"%label% %time%");
    GetSystemTime(&tz.utc); wcscpy(tz.zone_id, L"Eastern Standard Time");
    if (convert(&tz)) { printf("FAIL: eastern expected time\n"); return 1; }
    WCHAR expected[128]; swprintf(expected, 128, L"USA %02u:%02u", tz.local.wHour, tz.local.wMinute);
    if (install(&o) || !test_clock_time(display_time, 128) || wcscmp(display_time, expected) || uninstall(NULL)) {
        printf("FAIL: independently selected first time zone\n"); return 1;
    }
    wcscpy(o.local_zone_id, L"Nepal Standard Time");
    wcscpy(o.top_template, L"%label% %offset%"); wcscpy(o.bottom_template, L"%weekday% %offset%");
    if (install(&o) || !test_clock_time(display_time, 128) || wcscmp(display_time, L"USA UTC+05:45") ||
        !test_clock_date(display_date, 128) || !wcsstr(display_date, L"UTC+09:00") || uninstall(NULL)) {
        printf("FAIL: weekday and UTC-offset modules\n"); return 1;
    }
    o.target_zone_id[0] = 0;
    wcscpy(o.bottom_template, L"%label% %time%");
    SYSTEMTIME system_before, system_after; WCHAR before_text[128], after_text[128];
    GetLocalTime(&system_before);
    swprintf(before_text, 128, L"CHINA %02u:%02u", system_before.wHour, system_before.wMinute);
    if (install(&o) || !test_clock_date(display_date, 128)) { printf("FAIL: system as lower time zone\n"); return 1; }
    GetLocalTime(&system_after);
    swprintf(after_text, 128, L"CHINA %02u:%02u", system_after.wHour, system_after.wMinute);
    if ((wcscmp(display_date, before_text) && wcscmp(display_date, after_text)) || uninstall(NULL)) {
        printf("FAIL: lower system time differs from Windows\n"); return 1;
    }
    o.top_is_china = 1;
    wcscpy(o.top_template, L"%label% %time%");
    GetLocalTime(&system_before);
    swprintf(before_text, 128, L"CHINA %02u:%02u", system_before.wHour, system_before.wMinute);
    if (install(&o) || !test_clock_time(display_time, 128)) { printf("FAIL: system as upper time zone\n"); return 1; }
    GetLocalTime(&system_after);
    swprintf(after_text, 128, L"CHINA %02u:%02u", system_after.wHour, system_after.wMinute);
    if ((wcscmp(display_time, before_text) && wcscmp(display_time, after_text)) || uninstall(NULL)) {
        printf("FAIL: upper system time differs from Windows\n"); return 1;
    }
    o.reserved = 1; o.top_is_china = 0;
    wcscpy(o.top_template, L"%time%%second%"); wcscpy(o.bottom_template, L"%time%%second%");
    if (!o.test_local_return || install(&o)) { printf("FAIL: install second scheduler hook\n"); return 1; }
    SYSTEMTIME scheduled, unrelated, before, after;
    test_clock_schedule(&scheduled);
    if (scheduled.wSecond != 59) { printf("FAIL: scheduler interval is not one second\n"); return 1; }
    GetLocalTime(&before); test_other_local(&unrelated); GetLocalTime(&after);
    if (unrelated.wSecond != before.wSecond && unrelated.wSecond != after.wSecond) {
        printf("FAIL: unrelated local time was modified\n"); return 1;
    }
    GetLocalTime(&before);
    if (!test_clock_date(display_date, 128)) { printf("FAIL: lower seconds format\n"); return 1; }
    GetLocalTime(&after);
    swprintf(before_text, 128, L"%02u:%02u:%02u", before.wHour, before.wMinute, before.wSecond);
    swprintf(after_text, 128, L"%02u:%02u:%02u", after.wHour, after.wMinute, after.wSecond);
    if (wcscmp(display_date, before_text) && wcscmp(display_date, after_text)) {
        printf("FAIL: lower seconds do not match Windows time\n"); return 1;
    }
    WCHAR previous_line[128]; wcscpy(previous_line, display_date);
    Sleep(1100);
    if (!test_clock_date(display_date, 128) || !wcscmp(previous_line, display_date)) {
        printf("FAIL: displayed seconds did not advance\n"); return 1;
    }
    if (uninstall(NULL) || *(void **)find_import(self, "GetLocalTime") != (void *)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetLocalTime")) {
        printf("FAIL: seconds scheduler was not restored\n"); return 1;
    }
    wcscpy(o.bottom_template, L"%second%%time%");
    if (install(&o)) { printf("FAIL: reinstall reordered seconds\n"); return 1; }
    GetLocalTime(&before); test_clock_date(display_date, 128); GetLocalTime(&after);
    swprintf(before_text, 128, L"%02u%02u:%02u", before.wSecond, before.wHour, before.wMinute);
    swprintf(after_text, 128, L"%02u%02u:%02u", after.wSecond, after.wHour, after.wMinute);
    if ((wcscmp(display_date, before_text) && wcscmp(display_date, after_text)) || uninstall(NULL)) {
        printf("FAIL: reordered seconds rendering or restoration\n"); return 1;
    }
    printf("PASS: DST, target zones, seconds, scheduler isolation, templates, buffers and restoration\n");
    return 0;
}
