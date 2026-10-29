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
#ifdef CLOCK_TEST
    uintptr_t test_module;
    uintptr_t test_time_return;
    uintptr_t test_date_return;
#endif
} ClockOptions;

typedef int (WINAPI *FormatTimeFn)(LPCWSTR,DWORD,const SYSTEMTIME*,LPCWSTR,LPWSTR,int);
typedef int (WINAPI *FormatDateFn)(LPCWSTR,DWORD,const SYSTEMTIME*,LPCWSTR,LPWSTR,int,LPCWSTR);

static FormatTimeFn g_original_time;
static FormatDateFn g_original_date;
static PVOID volatile *g_time_slot;
static PVOID volatile *g_date_slot;
static HMODULE g_tray;
static ClockOptions g_options;
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

static BOOL china_time(SYSTEMTIME *out) {
    FILETIME ft;
    ULARGE_INTEGER value;
    GetSystemTimeAsFileTime(&ft);
    value.LowPart = ft.dwLowDateTime;
    value.HighPart = ft.dwHighDateTime;
    value.QuadPart += 8ULL * 60 * 60 * 10000000ULL;
    ft.dwLowDateTime = value.LowPart;
    ft.dwHighDateTime = value.HighPart;
    return FileTimeToSystemTime(&ft, out);
}

static BOOL render_line(BOOL upper, WCHAR *line, size_t capacity) {
    BOOL use_china = upper ? g_options.top_is_china != 0 : g_options.top_is_china == 0;
    const WCHAR *label = use_china ? g_options.china_label : g_options.local_label;
    const WCHAR *format = upper ? g_options.top_template : g_options.bottom_template;
    SYSTEMTIME value;
    WCHAR clock[128], date[128];
    if (use_china) { if (!china_time(&value)) return FALSE; }
    else GetLocalTime(&value);
    if (!g_original_time(NULL, 0, &value, g_options.time_format, clock, 128) ||
        !g_original_date(NULL, 0, &value, g_options.date_format, date, 128, NULL)) return FALSE;
    size_t used = 0;
    while (*format) {
        const WCHAR *replacement = NULL;
        size_t advance = 1;
        if (!wcsncmp(format, L"%label%", 7)) { replacement = label; advance = 7; }
        else if (!wcsncmp(format, L"%time%", 6)) { replacement = clock; advance = 6; }
        else if (!wcsncmp(format, L"%date%", 6)) { replacement = date; advance = 6; }
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
        format += advance;
    }
    line[used] = 0;
    return TRUE;
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
        restored = time_ok && date_ok;
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
        !options->time_format[0] || !options->date_format[0] || options->top_is_china > 1)
        return STATUS_BAD_ARGUMENT;
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
    if (!find_imports(g_tray)) { ReleaseSRWLockExclusive(&g_lock); return STATUS_IMPORT_MISSING; }
#ifndef CLOCK_TEST
    if (!validate_calls(TIME_CALLS, sizeof(TIME_CALLS)/sizeof(TIME_CALLS[0]), g_time_slot) ||
        !validate_calls(DATE_CALLS, sizeof(DATE_CALLS)/sizeof(DATE_CALLS[0]), g_date_slot)) {
        ReleaseSRWLockExclusive(&g_lock); return STATUS_UNSUPPORTED_MODULE;
    }
#endif
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    g_original_time = (FormatTimeFn)GetProcAddress(kernel, "GetTimeFormatEx");
    g_original_date = (FormatDateFn)GetProcAddress(kernel, "GetDateFormatEx");
    if (!g_original_time || !g_original_date || *g_time_slot != (PVOID)g_original_time ||
        *g_date_slot != (PVOID)g_original_date) {
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
    HANDLE monitor = CreateThread(NULL, 0, monitor_controller, monitor_data, 0, NULL);
    if (!monitor) {
        InterlockedExchange(&g_active, 0);
        replace_slot(g_time_slot, (PVOID)hooked_time, (PVOID)g_original_time);
        replace_slot(g_date_slot, (PVOID)hooked_date, (PVOID)g_original_date);
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
