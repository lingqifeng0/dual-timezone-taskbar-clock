using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NativeTaskbarClock
{
    internal static class SettingsPipe
    {
        private static string Name
        {
            get { return "DualZoneClockV2_" + WindowsIdentity.GetCurrent().User.Value + "_" + Process.GetCurrentProcess().SessionId; }
        }

        public static void Apply(ClockSettings settings)
        {
            settings.Validate();
            Task<string> task = Task.Factory.StartNew(delegate {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut)) {
                    pipe.Connect(5000);
                    using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true)) {
                        writer.AutoFlush = true;
                        writer.WriteLine(new JavaScriptSerializer().Serialize(settings));
                        return reader.ReadLine();
                    }
                }
            });
            bool done;
            try { done = task.Wait(30000); }
            catch (AggregateException ex) { throw new InvalidOperationException("无法连接正在运行的时钟程序。请确认程序已启动。", ex); }
            if (!done) throw new InvalidOperationException("时钟程序响应超时，设置状态未确认。请重新打开设置检查。");
            string reply = task.Result;
            if (reply == null) throw new InvalidOperationException("时钟程序没有返回设置结果。");
            var response = new JavaScriptSerializer().Deserialize<Reply>(reply);
            if (response == null || !response.Ok) throw new InvalidOperationException(response == null ? "设置返回结果无效。" : response.Error);
        }

        public sealed class Reply { public bool Ok; public string Error; }

        internal sealed class Server : IDisposable
        {
            private readonly Action<ClockSettings> apply;
            private readonly Control dispatcher;
            private readonly Thread thread;
            private readonly object sync = new object();
            private NamedPipeServerStream current;
            private volatile bool stopping;

            public Server(Control uiDispatcher, Action<ClockSettings> applySettings)
            {
                dispatcher = uiDispatcher; apply = applySettings;
                thread = new Thread(Run); thread.IsBackground = true; thread.Start();
            }

            private void Run()
            {
                while (!stopping) {
                    try {
                        PipeSecurity security = new PipeSecurity();
                        security.SetAccessRuleProtection(true, false);
                        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                        using (NamedPipeServerStream pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security)) {
                            lock (sync) { if (stopping) return; current = pipe; }
                            pipe.WaitForConnection();
                            using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                            using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true)) {
                                writer.AutoFlush = true;
                                string request = reader.ReadLine();
                                Reply reply = new Reply();
                                if (request == null || request.Length > 16000) {
                                    reply.Error = "设置请求无效。";
                                } else {
                                    ClockSettings settings = new JavaScriptSerializer().Deserialize<ClockSettings>(request);
                                    if (settings == null) reply.Error = "设置请求无效。";
                                    else {
                                        ManualResetEvent done = new ManualResetEvent(false);
                                        try {
                                            dispatcher.BeginInvoke(new Action(delegate {
                                                try { apply(settings); reply.Ok = true; }
                                                catch (Exception ex) { reply.Error = ex.Message; }
                                                finally { done.Set(); }
                                            }));
                                            done.WaitOne();
                                        } finally { done.Dispose(); }
                                    }
                                }
                                writer.WriteLine(new JavaScriptSerializer().Serialize(reply));
                            }
                        }
                    } catch (ObjectDisposedException) { if (stopping) return; }
                    catch (Exception) { if (stopping) return; Thread.Sleep(200); }
                    finally { lock (sync) { current = null; } }
                }
            }

            public void Dispose()
            {
                stopping = true;
                lock (sync) { if (current != null) current.Dispose(); }
            }
        }
    }
}
