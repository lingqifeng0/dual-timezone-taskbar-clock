/* Remote-injection test target: it is a child test process, never Explorer. */
#define main isolated_test_main
#include "test_clock_hook.c"
#undef main

int main(void) {
    HMODULE self = GetModuleHandleW(NULL);
    uintptr_t time_return = find_return(test_clock_time, find_import(self, "GetTimeFormatEx"));
    uintptr_t date_return = find_return(test_clock_date, find_import(self, "GetDateFormatEx"));
    if (!time_return || !date_return) return 1;
    printf("READY %lu %llu %llu %llu\n", GetCurrentProcessId(),
           (unsigned long long)(uintptr_t)self,
           (unsigned long long)time_return, (unsigned long long)date_return);
    fflush(stdout);
    for (int i=0; i<120; ++i) {
        WCHAR t[128], d[128], other[128];
        int a = test_clock_time(t, 128);
        int b = test_clock_date(d, 128);
        int c = test_unrelated_time(other, 128);
        printf("FRAME %d %d %d\n", a && !wcsncmp(t, L"USA ", 4),
               b && !wcsncmp(d, L"CHINA ", 6), c && !wcsncmp(other, L"USA ", 4));
        fflush(stdout);
        Sleep(250);
    }
    return 0;
}
