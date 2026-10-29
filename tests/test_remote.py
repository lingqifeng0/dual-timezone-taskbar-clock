"""Exercise remote load, install, controller-death cleanup and restore on a child fixture."""
import ctypes
from ctypes import wintypes
from pathlib import Path
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
k = ctypes.WinDLL("kernel32", use_last_error=True)

class MODULEENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wintypes.DWORD), ("th32ModuleID", wintypes.DWORD),
                ("th32ProcessID", wintypes.DWORD), ("GlblcntUsage", wintypes.DWORD),
                ("ProccntUsage", wintypes.DWORD), ("modBaseAddr", ctypes.c_void_p),
                ("modBaseSize", wintypes.DWORD), ("hModule", ctypes.c_void_p),
                ("szModule", wintypes.WCHAR * 256), ("szExePath", wintypes.WCHAR * 260)]

class ClockOptions(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("controller_pid", wintypes.DWORD),
                ("top_is_china", wintypes.DWORD), ("reserved", wintypes.DWORD),
                ("local_label", wintypes.WCHAR * 32), ("china_label", wintypes.WCHAR * 32),
                ("time_format", wintypes.WCHAR * 64), ("date_format", wintypes.WCHAR * 64),
                ("top_template", wintypes.WCHAR * 96), ("bottom_template", wintypes.WCHAR * 96),
                ("local_zone_id", wintypes.WCHAR * 128), ("target_zone_id", wintypes.WCHAR * 128),
                ("test_module", ctypes.c_size_t), ("test_time_return", ctypes.c_size_t),
                ("test_date_return", ctypes.c_size_t), ("test_local_return", ctypes.c_size_t)]

k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k.OpenProcess.restype = wintypes.HANDLE
k.CloseHandle.argtypes = [wintypes.HANDLE]
k.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
k.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
k.Module32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MODULEENTRY32)]
k.Module32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(MODULEENTRY32)]
k.VirtualAllocEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]
k.VirtualAllocEx.restype = ctypes.c_void_p
k.VirtualFreeEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD]
k.WriteProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k.CreateRemoteThread.argtypes = [wintypes.HANDLE, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
k.CreateRemoteThread.restype = wintypes.HANDLE
k.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
k.GetExitCodeThread.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
k.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
k.GetModuleHandleW.restype = ctypes.c_void_p
k.GetProcAddress.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
k.GetProcAddress.restype = ctypes.c_void_p

def find_module(pid, name):
    snapshot = k.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    if snapshot == ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry = MODULEENTRY32(); entry.dwSize = ctypes.sizeof(entry)
        ok = k.Module32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            if entry.szModule.lower() == name.lower(): return entry.modBaseAddr
            ok = k.Module32NextW(snapshot, ctypes.byref(entry))
        return None
    finally: k.CloseHandle(snapshot)

def invoke(process, fn, data=None):
    ptr = None
    thread = None
    try:
        if data is not None:
            ptr = k.VirtualAllocEx(process, None, len(data), 0x3000, 4)
            if not ptr: raise ctypes.WinError(ctypes.get_last_error())
            written = ctypes.c_size_t()
            source = ctypes.create_string_buffer(data)
            if not k.WriteProcessMemory(process, ptr, source, len(data), ctypes.byref(written)) or written.value != len(data):
                raise ctypes.WinError(ctypes.get_last_error())
        thread_id = wintypes.DWORD()
        thread = k.CreateRemoteThread(process, None, 0, fn, ptr, 0, ctypes.byref(thread_id))
        if not thread: raise ctypes.WinError(ctypes.get_last_error())
        if k.WaitForSingleObject(thread, 15000) != 0: raise RuntimeError("remote call timed out")
        code = wintypes.DWORD()
        if not k.GetExitCodeThread(thread, ctypes.byref(code)): raise ctypes.WinError(ctypes.get_last_error())
        return code.value
    finally:
        if thread: k.CloseHandle(thread)
        if ptr: k.VirtualFreeEx(process, ptr, 0, 0x8000)

def wait_frame(proc, expected):
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline:
        line = proc.stdout.readline().strip()
        if line == expected: return
        if not line and proc.poll() is not None: break
    raise RuntimeError("expected %s; last output was %s" % (expected, line))

fixture = subprocess.Popen([str(HERE / "fixture_clock.exe")], cwd=HERE,
                           stdout=subprocess.PIPE, text=True, encoding="ascii")
guardian = None
process = None
try:
    ready = fixture.stdout.readline().strip().split()
    assert ready[0] == "READY", ready
    pid, base, t_return, d_return, l_return = map(int, ready[1:])
    process = k.OpenProcess(0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010, False, pid)
    if not process: raise ctypes.WinError(ctypes.get_last_error())
    local_base = k.GetModuleHandleW("KernelBase.dll")
    local_loader = k.GetProcAddress(local_base, b"LoadLibraryW")
    remote_base = find_module(pid, "KernelBase.dll")
    if not local_base or not local_loader or not remote_base: raise RuntimeError("loader unavailable")
    loader = remote_base + local_loader - local_base
    dll = str(HERE / "clock_hook.test.dll")
    invoke(process, loader, (dll + "\0").encode("utf-16-le"))
    remote_hook = find_module(pid, "clock_hook.test.dll")
    if not remote_hook: raise RuntimeError("test DLL did not load")
    local_hook = ctypes.WinDLL(dll)
    install = remote_hook + ctypes.cast(local_hook.InstallClockHook, ctypes.c_void_p).value - local_hook._handle
    uninstall = remote_hook + ctypes.cast(local_hook.UninstallClockHook, ctypes.c_void_p).value - local_hook._handle

    guardian = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"],
                                creationflags=subprocess.CREATE_NO_WINDOW)
    options = ClockOptions(ctypes.sizeof(ClockOptions), guardian.pid, 0, 1, "USA", "CHINA",
                           "HH:mm", "yyyy/MM/dd", "%label% %time%", "%label% %time%", "", "China Standard Time",
                           base, t_return, d_return, l_return)
    assert invoke(process, install, bytes(options)) == 0
    wait_frame(fixture, "FRAME 1 1 0 1")
    guardian.terminate(); guardian.wait(timeout=5); guardian = None
    wait_frame(fixture, "FRAME 0 0 0 0")

    options.controller_pid = k.GetCurrentProcessId()
    assert invoke(process, install, bytes(options)) == 0
    wait_frame(fixture, "FRAME 1 1 0 1")
    assert invoke(process, uninstall) == 0
    wait_frame(fixture, "FRAME 0 0 0 0")
    print("PASS: remote injection into fixture, controller-death cleanup, reapply and restoration")
finally:
    if guardian:
        guardian.terminate(); guardian.wait(timeout=5)
    if process: k.CloseHandle(process)
    fixture.terminate(); fixture.wait(timeout=5)
