"""Confirm a production module can be loaded into a child fixture process."""
import ctypes
from pathlib import Path
import subprocess
import sys

here = Path(__file__).resolve().parent
source = (here / 'test_remote.py').read_text(encoding='utf-8')
exec(compile(source.split('fixture = subprocess.Popen', 1)[0], str(here / 'test_remote.py'), 'exec'))

dll = Path(sys.argv[1]).resolve()
fixture = subprocess.Popen([str(here / 'fixture_clock.exe')], cwd=here,
                           stdout=subprocess.PIPE, text=True, encoding='ascii')
process = None
try:
    ready = fixture.stdout.readline().strip().split()
    if not ready or ready[0] != 'READY': raise RuntimeError('fixture did not start')
    pid = int(ready[1])
    process = k.OpenProcess(0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010, False, pid)
    if not process: raise ctypes.WinError(ctypes.get_last_error())
    local_base = k.GetModuleHandleW('KernelBase.dll')
    local_loader = k.GetProcAddress(local_base, b'LoadLibraryW')
    remote_base = find_module(pid, 'KernelBase.dll')
    result = invoke(process, remote_base + local_loader - local_base, (str(dll) + '\0').encode('utf-16-le'))
    actual = find_module(pid, dll.name)
    if not actual: raise RuntimeError('remote load returned %d, module not present' % result)
    print('PASS: production module loads into a child process')
finally:
    if process: k.CloseHandle(process)
    fixture.terminate(); fixture.wait(timeout=5)
