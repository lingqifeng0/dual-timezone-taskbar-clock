using System;
using System.Threading;
using System.Windows.Forms;
using NativeTaskbarClock;

internal static class IpcTest
{
    [STAThread] private static void Main()
    {
        Control dispatcher = new Control(); IntPtr handle = dispatcher.Handle;
        ApplicationContext app = new ApplicationContext();
        int count = 0; string failure = null;
        using (var server = new SettingsPipe.Server(dispatcher, delegate(ClockSettings s) {
            ++count;
            if (count == 1) {
                if (s.LocalZoneId != "Eastern Standard Time" || s.TargetZoneId != "Tokyo Standard Time" || !s.StartOnLogin)
                    throw new Exception("both time zones and startup option must arrive intact");
            } else throw new InvalidOperationException("expected rejection");
        })) {
            Thread client = new Thread(delegate() {
                try {
                    ClockSettings s = new ClockSettings();
                    s.LocalZoneId = "Eastern Standard Time"; s.TargetZoneId = "Tokyo Standard Time"; s.StartOnLogin = true;
                    SettingsPipe.Apply(s);
                    bool rejected = false;
                    try { SettingsPipe.Apply(s); } catch (InvalidOperationException ex) { rejected = ex.Message.Contains("expected rejection"); }
                    if (!rejected) throw new Exception("server rejection did not propagate");
                } catch (Exception ex) { failure = ex.ToString(); }
                finally { dispatcher.BeginInvoke(new Action(delegate { app.ExitThread(); })); }
            });
            client.IsBackground = true; client.Start(); Application.Run(app); client.Join(2000);
        }
        dispatcher.Dispose();
        if (failure != null || count != 2) throw new Exception(failure ?? "unexpected request count");
        Console.WriteLine("PASS: isolated settings IPC preserves both zones and propagates errors");
    }
}
