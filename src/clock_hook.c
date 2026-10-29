/*
 * Dual-zone taskbar clock: narrow, version-checked IAT hook.
 * This DLL is loaded into Explorer by the companion controller. It does not
 * modify files on disk. This build deliberately supports only a checked copy
 * of Windows 11's SystemTray.dll; unknown builds are rejected unchanged.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>
#include <strsafe.h>

#define STATUS_OK 0
#define STATUS_BAD_ARGUMENT 1
#define STATUS_UNSUPPORTED_MODULE 2
#define STATUS_ALREADY_ACTIVE 3
#define STATUS_IMPORT_MISSING 4
#define STATUS_CONFLICT 5
#define STATUS_PROTECT_FAILED 6
#define STATUS_MONITOR_FAILED 7
#define STATUS_INVALID_TIMEZONE 8

typedef struct ClockOptions {
    DWORD size;
    DWORD controller_pid;
    DWORD top_is_china;
    DWORD reserved;
    WCHAR local_label[32];
    WCHAR china_label[32];
    WCHAR time_format[64];
    WCHAR date_format[64];
    WCHAR top_template[96];
    WCHAR bottom_template[96];
    WCHAR local_zone_id[128];
    WCHAR target_zone_id[128];
#ifdef CLOCK_TEST
    uintptr_t test_module;
    uintptr_t test_time_return;
    uintptr_t test_date_return;
    uintptr_t test_local_return;
#endif
} ClockOptions;

typedef int (WINAPI *FormatTimeFn)(LPCWSTR,DWORD,const SYSTEMTIME*,LPCWSTR,LPWSTR,int);
typedef int (WINAPI *FormatDateFn)(LPCWSTR,DWORD,const SYSTEMTIME*,LPCWSTR,LPWSTR,int,LPCWSTR);
typedef VOID (WINAPI *LocalTimeFn)(LPSYSTEMTIME);

static FormatTimeFn g_original_time;
static FormatDateFn g_original_date;
static LocalTimeFn g_original_local;
static PVOID volatile *g_time_slot;
static PVOID volatile *g_date_slot;
static PVOID volatile *g_local_slot;
static HMODULE g_tray;
static ClockOptions g_options;
static DYNAMIC_TIME_ZONE_INFORMATION g_target_zone;
static DYNAMIC_TIME_ZONE_INFORMATION g_local_zone;
static BOOL g_use_system_zone;
static BOOL g_target_uses_system_zone;
static volatile LONG g_active;
static volatile LONG g_inflight;
static unsigned long long g_generation;
static HANDLE g_stop_event;
static SRWLOCK g_lock = SRWLOCK_INIT;
#ifdef CLOCK_DIAGNOSTICS
static volatile LONG diag_time_all, diag_time_match, diag_date_all, diag_date_match;
static volatile LONG diag_last_time_rva, diag_last_date_rva;
#endif

#ifndef CLOCK_TEST
/* Call-site offsets verified against SystemTray.dll build 26200.9711.
 * The on-disk SHA-256 and the in-memory image header are checked by the
 * controller and DLL respectively. Every instruction is verified to address
 * the expected import slot before the hook can be enabled. */
static const DWORD TIME_CALLS[] = {0xDA7F3,0xDABC7,0xDB043,0xDB05B,0x1322F7,0x13259F,0x1325B7};
static const DWORD DATE_CALLS[] = {0xDEA77,0xDEC41,0xDEC83,0xDF1E2,0x13520E,0x1353CC,0x135409};
/* GetLocalTime in the two verified ScheduleNextUpdate implementations. */
static const DWORD SCHEDULE_CALLS[] = {0xDD0CB,0x1343BF};
#endif

static int copy_result(const WCHAR *text, LPWSTR output, int capacity) {
    size_t length = wcslen(text) + 1;
    if (capacity == 0) return (int)length;
    if (!output || capacity < 0 || (size_t)capacity < length) {
        SetLastError(ERROR_INSUFFICIENT_BUFFER);
        return 0;
    }
    memcpy(output, text, length * sizeof(WCHAR));
    return (int)length;
}

static BOOL is_clock_call(void *return_address, BOOL is_time) {
#ifdef CLOCK_TEST
    return (uintptr_t)return_address == (is_time ? g_options.test_time_return : g_options.test_date_return);
#else
    const DWORD *list = is_time ? TIME_CALLS : DATE_CALLS;
    size_t count = is_time ? sizeof(TIME_CALLS)/sizeof(TIME_CALLS[0]) : sizeof(DATE_CALLS)/sizeof(DATE_CALLS[0]);
    uintptr_t address = (uintptr_t)return_address, base = (uintptr_t)g_tray;
    for (size_t i = 0; i < count; ++i) {
        if (address == base + list[i] + 6) return TRUE;
    }
    return FALSE;
#endif
}

static BOOL find_timezone(const WCHAR *id, DYNAMIC_TIME_ZONE_INFORMATION *result) {
    DYNAMIC_TIME_ZONE_INFORMATION info;
    for (DWORD i = 0; EnumDynamicTimeZoneInformation(i, &info) == ERROR_SUCCESS; ++i) {
        if (_wcsicmp(info.TimeZoneKeyName, id) == 0) { *result = info; return TRUE; }
    }
    return FALSE;
}

static BOOL render_line(BOOL upper, WCHAR *line, size_t capacity) {
    BOOL use_china = upper ? g_options.top_is_china != 0 : g_options.top_is_china == 0;
    const WCHAR *label = use_china ? g_options.china_label : g_options.local_label;
    const WCHAR *format = upper ? g_options.top_template : g_options.bottom_template;
    SYSTEMTIME utc, value;
    DYNAMIC_TIME_ZONE_INFORMATION system_zone;
    const DYNAMIC_TIME_ZONE_INFORMATION *zone = use_china ? &g_target_zone : &g_local_zone;
    if ((use_china && g_target_uses_system_zone) || (!use_china && g_use_system_zone)) {
        if (GetDynamicTimeZoneInformation(&system_zone) == TIME_ZONE_ID_INVALID) return FALSE;
        zone = &system_zone;
    }
    GetSystemTime(&utc);
    if (!SystemTimeToTzSpecificLocalTimeEx(zone, &utc, &value)) return FALSE;
    WCHAR clock[128], date[128], weekday[128], offset[32], second[8];
    if (!g_original_time(NULL, 0, &value, g_options.time_format, clock, 128) ||
        !g_original_date(NULL, 0, &value, g_options.date_format, date, 128, NULL) ||
        !g_original_date(NULL, 0, &value, L"dddd", weekday, 128, NULL)) return FALSE;
    FILETIME utc_file, local_file;
    if (!SystemTimeToFileTime(&utc, &utc_file) || !SystemTimeToFileTime(&value, &local_file)) return FALSE;
    ULARGE_INTEGER utc_ticks, local_ticks;
    utc_ticks.LowPart = utc_file.dwLowDateTime; utc_ticks.HighPart = utc_file.dwHighDateTime;
    local_ticks.LowPart = local_file.dwLowDateTime; local_ticks.HighPart = local_file.dwHighDateTime;
    LONGLONG delta = (LONGLONG)local_ticks.QuadPart - (LONGLONG)utc_ticks.QuadPart;
    int minutes = (int)(delta / 600000000LL), abs_minutes = minutes < 0 ? -minutes : minutes;
    if (FAILED(StringCchPrintfW(offset, 32, L"UTC%c%02d:%02d", minutes < 0 ? L'-' : L'+', abs_minutes / 60, abs_minutes % 60))) return FALSE;
    size_t used = 0;
    BOOL previous_was_time = FALSE;
    while (*format) {
        const WCHAR *replacement = NULL;
        size_t advance = 1;
        BOOL this_is_time = FALSE;
        if (!wcsncmp(format, L"%label%", 7)) { replacement = label; advance = 7; }
        else if (!wcsncmp(format, L"%time%", 6)) { replacement = clock; advance = 6; this_is_time = TRUE; }
        else if (!wcsncmp(format, L"%date%", 6)) { replacement = date; advance = 6; }
        else if (!wcsncmp(format, L"%weekday%", 9)) { replacement = weekday; advance = 9; }
        else if (!wcsncmp(format, L"%offset%", 8)) { replacement = offset; advance = 8; }
        else if (!wcsncmp(format, L"%second%", 8)) {
            if (FAILED(StringCchPrintfW(second, 8, L"%s%02u", previous_was_time ? L":" : L"",
                (unsigned)value.wSecond))) return FALSE;
            replacement = second; advance = 8;
        }
        else if (!wcsncmp(format, L"%%", 2)) { replacement = L"%"; advance = 2; }
        if (replacement) {
            size_t count = wcslen(replacement);
            if (used + count >= capacity) return FALSE;
            memcpy(line + used, replacement, count * sizeof(WCHAR));
            used += count;
        } else {
            if (used + 1 >= capacity) return FALSE;
            line[used++] = *format;
        }
        previous_was_time = this_is_time;
        format += advance;
    }
    line[used] = 0;
    return TRUE;
}

/* The original scheduler uses 60 - wSecond when Windows' own seconds option
 * is off. Restrict the changed value to its two verified call sites, giving a
 * one-second interval without changing the Windows setting or writing the
 * registry repeatedly. All other callers receive the actual local time. */
static VOID WINAPI hooked_local(LPSYSTEMTIME time) {
    void *caller = __builtin_return_address(0);
    InterlockedIncrement(&g_inflight);
    g_original_local(time);
    if (time && InterlockedCompareExchange(&g_active, 0, 0) && g_options.reserved == 1) {
#ifdef CLOCK_TEST
        if ((uintptr_t)caller == g_options.test_local_return) time->wSecond = 59;
#else
        uintptr_t address = (uintptr_t)caller, base = (uintptr_t)g_tray;
        for (size_t i = 0; i < sizeof(SCHEDULE_CALLS)/sizeof(SCHEDULE_CALLS[0]); ++i)
            if (address == base + SCHEDULE_CALLS[i] + 6) { time->wSecond = 59; break; }
#endif
    }
    InterlockedDecrement(&g_inflight);
}

static int WINAPI hooked_time(LPCWSTR locale, DWORD flags, const SYSTEMTIME *time,
                              LPCWSTR format, LPWSTR result, int length) {
    void *caller = __builtin_return_address(0);
    InterlockedIncrement(&g_inflight);
#ifdef CLOCK_DIAGNOSTICS
    InterlockedIncrement(&diag_time_all);
    InterlockedExchange(&diag_last_time_rva, (LONG)((uintptr_t)caller - (uintptr_t)g_tray));
    if (is_clock_call(caller, TRUE)) InterlockedIncrement(&diag_time_match);
#endif
    if (!InterlockedCompareExchange(&g_active, 0, 0) || !is_clock_call(caller, TRUE)) {
        InterlockedDecrement(&g_inflight);
        return g_original_time(locale, flags, time, format, result, length);
    }
    WCHAR line[512];
    if (!render_line(TRUE, line, 512)) {
        InterlockedDecrement(&g_inflight);
        return g_original_time(locale, flags, time, format, result, length);
    }
    int ret = copy_result(line, result, length);
    InterlockedDecrement(&g_inflight);
    return ret;
}

static int WINAPI hooked_date(LPCWSTR locale, DWORD flags, const SYSTEMTIME *date,
                              LPCWSTR format, LPWSTR result, int length, LPCWSTR calendar) {
    void *caller = __builtin_return_address(0);
    InterlockedIncrement(&g_inflight);
#ifdef CLOCK_DIAGNOSTICS
    InterlockedIncrement(&diag_date_all);
    InterlockedExchange(&diag_last_date_rva, (LONG)((uintptr_t)caller - (uintptr_t)g_tray));
    if (is_clock_call(caller, FALSE) && !(flags & DATE_LONGDATE)) InterlockedIncrement(&diag_date_match);
#endif
    if (!InterlockedCompareExchange(&g_active, 0, 0) || !is_clock_call(caller, FALSE) || (flags & DATE_LONGDATE)) {
        InterlockedDecrement(&g_inflight);
        return g_original_date(locale, flags, date, format, result, length, calendar);
    }
    WCHAR line[512];
    if (!render_line(FALSE, line, 512)) {
        InterlockedDecrement(&g_inflight);
        return g_original_date(locale, flags, date, format, result, length, calendar);
    }
    int ret = copy_result(line, result, length);
    InterlockedDecrement(&g_inflight);
    return ret;
}

static BOOL find_imports(HMODULE module) {
    BYTE *base = (BYTE *)module;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE) return FALSE;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return FALSE;
#ifndef CLOCK_TEST
    if (nt->FileHeader.TimeDateStamp != 0x6aa9e58a || nt->OptionalHeader.SizeOfImage != 0x20a000) return FALSE;
#endif
    DWORD rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!rva) return FALSE;
    IMAGE_IMPORT_DESCRIPTOR *desc = (IMAGE_IMPORT_DESCRIPTOR *)(base + rva);
    for (; desc->Name; ++desc) {
        if (!desc->OriginalFirstThunk) continue;
        IMAGE_THUNK_DATA64 *names = (IMAGE_THUNK_DATA64 *)(base + desc->OriginalFirstThunk);
        IMAGE_THUNK_DATA64 *slots = (IMAGE_THUNK_DATA64 *)(base + desc->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL64(names->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME *by_name = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
            if (strcmp((char *)by_name->Name, "GetTimeFormatEx") == 0)
                g_time_slot = (PVOID volatile *)&slots->u1.Function;
            if (strcmp((char *)by_name->Name, "GetDateFormatEx") == 0)
                g_date_slot = (PVOID volatile *)&slots->u1.Function;
            if (strcmp((char *)by_name->Name, "GetLocalTime") == 0)
                g_local_slot = (PVOID volatile *)&slots->u1.Function;
        }
    }
    return g_time_slot && g_date_slot;
}

#ifndef CLOCK_TEST
static BOOL validate_calls(const DWORD *calls, size_t count, PVOID volatile *slot) {
    BYTE *base = (BYTE *)g_tray;
    for (size_t i = 0; i < count; ++i) {
        BYTE *instruction = base + calls[i];
        int32_t displacement;
        if (instruction[0] != 0xff || instruction[1] != 0x15) return FALSE;
        memcpy(&displacement, instruction + 2, sizeof(displacement));
        if ((void *)(instruction + 6 + displacement) != (void *)slot) return FALSE;
    }
    return TRUE;
}
#endif

static BOOL replace_slot(PVOID volatile *slot, PVOID expected, PVOID value) {
    DWORD old_protection, ignored;
    if (!VirtualProtect((void *)slot, sizeof(PVOID), PAGE_READWRITE, &old_protection)) return FALSE;
    PVOID previous = InterlockedCompareExchangePointer(slot, value, expected);
    VirtualProtect((void *)slot, sizeof(PVOID), old_protection, &ignored);
    return previous == expected;
}

static BOOL stop_internal(unsigned long long generation) {
    BOOL restored = TRUE;
    AcquireSRWLockExclusive(&g_lock);
    if ((!generation || generation == g_generation) && InterlockedExchange(&g_active, 0)) {
        BOOL time_ok = replace_slot(g_time_slot, (PVOID)hooked_time, (PVOID)g_original_time);
        BOOL date_ok = replace_slot(g_date_slot, (PVOID)hooked_date, (PVOID)g_original_date);
        BOOL local_ok = !g_options.reserved || replace_slot(g_local_slot, (PVOID)hooked_local, (PVOID)g_original_local);
        restored = time_ok && date_ok && local_ok;
        if (g_stop_event) SetEvent(g_stop_event);
    }
    ReleaseSRWLockExclusive(&g_lock);
    return restored;
}

typedef struct MonitorData { HANDLE process; HANDLE stop_event; unsigned long long generation; } MonitorData;
static DWORD WINAPI monitor_controller(void *parameter) {
    MonitorData *data = (MonitorData *)parameter;
    HANDLE handles[2] = {data->process, data->stop_event};
    DWORD result = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
    if (result == WAIT_OBJECT_0) stop_internal(data->generation);
    AcquireSRWLockExclusive(&g_lock);
    if (g_generation == data->generation && g_stop_event == data->stop_event) g_stop_event = NULL;
    ReleaseSRWLockExclusive(&g_lock);
    CloseHandle(data->process);
    CloseHandle(data->stop_event);
    HeapFree(GetProcessHeap(), 0, data);
    return 0;
}

__declspec(dllexport) DWORD WINAPI InstallClockHook(void *parameter) {
    ClockOptions *options = (ClockOptions *)parameter;
    if (!options || options->size != sizeof(ClockOptions) || !options->controller_pid ||
        !wmemchr(options->local_label, 0, 32) || !wmemchr(options->china_label, 0, 32) ||
        !wmemchr(options->time_format, 0, 64) || !wmemchr(options->date_format, 0, 64) ||
        !wmemchr(options->top_template, 0, 96) || !wmemchr(options->bottom_template, 0, 96) ||
        !wmemchr(options->local_zone_id, 0, 128) || !wmemchr(options->target_zone_id, 0, 128) ||
        !options->time_format[0] || !options->date_format[0] || options->top_is_china > 1 || options->reserved > 1)
        return STATUS_BAD_ARGUMENT;
    DYNAMIC_TIME_ZONE_INFORMATION target_zone = {0};
    if (options->target_zone_id[0] && !find_timezone(options->target_zone_id, &target_zone)) return STATUS_INVALID_TIMEZONE;
    DYNAMIC_TIME_ZONE_INFORMATION local_zone = {0};
    if (options->local_zone_id[0] && !find_timezone(options->local_zone_id, &local_zone)) return STATUS_INVALID_TIMEZONE;
    AcquireSRWLockExclusive(&g_lock);
    if (g_active) { ReleaseSRWLockExclusive(&g_lock); return STATUS_ALREADY_ACTIVE; }
#ifdef CLOCK_TEST
    g_tray = (HMODULE)options->test_module;
#else
    g_tray = GetModuleHandleW(L"SystemTray.dll");
#endif
    if (!g_tray) { ReleaseSRWLockExclusive(&g_lock); return STATUS_UNSUPPORTED_MODULE; }
    g_time_slot = NULL;
    g_date_slot = NULL;
    g_local_slot = NULL;
    if (!find_imports(g_tray)) { ReleaseSRWLockExclusive(&g_lock); return STATUS_IMPORT_MISSING; }
    if (options->reserved && !g_local_slot) { ReleaseSRWLockExclusive(&g_lock); return STATUS_IMPORT_MISSING; }
#ifndef CLOCK_TEST
    if (!validate_calls(TIME_CALLS, sizeof(TIME_CALLS)/sizeof(TIME_CALLS[0]), g_time_slot) ||
        !validate_calls(DATE_CALLS, sizeof(DATE_CALLS)/sizeof(DATE_CALLS[0]), g_date_slot)) {
        ReleaseSRWLockExclusive(&g_lock); return STATUS_UNSUPPORTED_MODULE;
    }
    if (options->reserved && !validate_calls(SCHEDULE_CALLS, sizeof(SCHEDULE_CALLS)/sizeof(SCHEDULE_CALLS[0]), g_local_slot)) {
        ReleaseSRWLockExclusive(&g_lock); return STATUS_UNSUPPORTED_MODULE;
    }
#endif
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    g_original_time = (FormatTimeFn)GetProcAddress(kernel, "GetTimeFormatEx");
    g_original_date = (FormatDateFn)GetProcAddress(kernel, "GetDateFormatEx");
    g_original_local = (LocalTimeFn)GetProcAddress(kernel, "GetLocalTime");
    if (!g_original_time || !g_original_date || *g_time_slot != (PVOID)g_original_time ||
        *g_date_slot != (PVOID)g_original_date ||
        (options->reserved && (!g_original_local || *g_local_slot != (PVOID)g_original_local))) {
        ReleaseSRWLockExclusive(&g_lock); return STATUS_CONFLICT;
    }
    HANDLE controller = OpenProcess(SYNCHRONIZE, FALSE, options->controller_pid);
    if (!controller) { ReleaseSRWLockExclusive(&g_lock); return STATUS_MONITOR_FAILED; }
    // Prior callbacks use the previous immutable settings until they leave.
    for (int n = 0; InterlockedCompareExchange(&g_inflight, 0, 0) && n < 200; ++n) Sleep(10);
    if (InterlockedCompareExchange(&g_inflight, 0, 0)) {
        CloseHandle(controller); ReleaseSRWLockExclusive(&g_lock); return STATUS_CONFLICT;
    }
    MonitorData *monitor_data = (MonitorData *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(MonitorData));
    if (!monitor_data) { CloseHandle(controller); ReleaseSRWLockExclusive(&g_lock); return STATUS_MONITOR_FAILED; }
    monitor_data->process = controller;
    monitor_data->stop_event = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!monitor_data->stop_event) {
        CloseHandle(controller); HeapFree(GetProcessHeap(), 0, monitor_data);
        ReleaseSRWLockExclusive(&g_lock); return STATUS_MONITOR_FAILED;
    }
    monitor_data->generation = ++g_generation;
    g_stop_event = monitor_data->stop_event;
    g_options = *options;
    g_target_zone = target_zone;
    g_local_zone = local_zone;
    g_use_system_zone = !options->local_zone_id[0];
    g_target_uses_system_zone = !options->target_zone_id[0];
    InterlockedExchange(&g_active, 1);
    if (!replace_slot(g_time_slot, (PVOID)g_original_time, (PVOID)hooked_time)) {
        InterlockedExchange(&g_active, 0); g_stop_event = NULL; CloseHandle(controller);
        CloseHandle(monitor_data->stop_event); HeapFree(GetProcessHeap(), 0, monitor_data);
        ReleaseSRWLockExclusive(&g_lock); return STATUS_PROTECT_FAILED;
    }
    if (!replace_slot(g_date_slot, (PVOID)g_original_date, (PVOID)hooked_date)) {
        InterlockedExchange(&g_active, 0);
        replace_slot(g_time_slot, (PVOID)hooked_time, (PVOID)g_original_time);
        g_stop_event = NULL; CloseHandle(controller); CloseHandle(monitor_data->stop_event); HeapFree(GetProcessHeap(), 0, monitor_data);
        ReleaseSRWLockExclusive(&g_lock); return STATUS_PROTECT_FAILED;
    }
    if (options->reserved && !replace_slot(g_local_slot, (PVOID)g_original_local, (PVOID)hooked_local)) {
        InterlockedExchange(&g_active, 0);
        replace_slot(g_time_slot, (PVOID)hooked_time, (PVOID)g_original_time);
        replace_slot(g_date_slot, (PVOID)hooked_date, (PVOID)g_original_date);
        g_stop_event = NULL; CloseHandle(controller); CloseHandle(monitor_data->stop_event); HeapFree(GetProcessHeap(), 0, monitor_data);
        ReleaseSRWLockExclusive(&g_lock); return STATUS_PROTECT_FAILED;
    }
    HANDLE monitor = CreateThread(NULL, 0, monitor_controller, monitor_data, 0, NULL);
    if (!monitor) {
        InterlockedExchange(&g_active, 0);
        replace_slot(g_time_slot, (PVOID)hooked_time, (PVOID)g_original_time);
        replace_slot(g_date_slot, (PVOID)hooked_date, (PVOID)g_original_date);
        if (options->reserved) replace_slot(g_local_slot, (PVOID)hooked_local, (PVOID)g_original_local);
        g_stop_event = NULL; CloseHandle(controller); CloseHandle(monitor_data->stop_event); HeapFree(GetProcessHeap(), 0, monitor_data);
        ReleaseSRWLockExclusive(&g_lock); return STATUS_MONITOR_FAILED;
    }
    CloseHandle(monitor);
    ReleaseSRWLockExclusive(&g_lock);
    return STATUS_OK;
}

__declspec(dllexport) DWORD WINAPI UninstallClockHook(void *unused) {
    (void)unused;
    return stop_internal(0) ? STATUS_OK : STATUS_PROTECT_FAILED;
}

#ifdef CLOCK_TEST
typedef struct TestTimezoneArgs {
    WCHAR zone_id[128];
    SYSTEMTIME utc;
    SYSTEMTIME local;
} TestTimezoneArgs;
__declspec(dllexport) DWORD WINAPI TestTimezoneConversion(void *parameter) {
    TestTimezoneArgs *args = (TestTimezoneArgs *)parameter;
    DYNAMIC_TIME_ZONE_INFORMATION zone;
    if (!args || !wmemchr(args->zone_id, 0, 128) || !find_timezone(args->zone_id, &zone)) return 1;
    return SystemTimeToTzSpecificLocalTimeEx(&zone, &args->utc, &args->local) ? 0 : 2;
}
#endif

#ifdef CLOCK_DIAGNOSTICS
__declspec(dllexport) DWORD WINAPI HookDiagnosticCounts(void *unused) {
    (void)unused;
    DWORD a = (DWORD)InterlockedCompareExchange(&diag_time_all, 0, 0);
    DWORD b = (DWORD)InterlockedCompareExchange(&diag_time_match, 0, 0);
    DWORD c = (DWORD)InterlockedCompareExchange(&diag_date_all, 0, 0);
    DWORD d = (DWORD)InterlockedCompareExchange(&diag_date_match, 0, 0);
    return (a > 255 ? 255 : a) | ((b > 255 ? 255 : b) << 8) |
           ((c > 255 ? 255 : c) << 16) | ((d > 255 ? 255 : d) << 24);
}
__declspec(dllexport) DWORD WINAPI HookDiagnosticTimeRva(void *unused) {
    (void)unused; return (DWORD)InterlockedCompareExchange(&diag_last_time_rva, 0, 0);
}
__declspec(dllexport) DWORD WINAPI HookDiagnosticDateRva(void *unused) {
    (void)unused; return (DWORD)InterlockedCompareExchange(&diag_last_date_rva, 0, 0);
}
#endif

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
