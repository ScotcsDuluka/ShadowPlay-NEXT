using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
static void Check(string label,bool condition)
{
    if(!condition)throw new Exception("FAILED "+label);
    Console.WriteLine("PASS "+label);
}
Check("Completed load allows cleanup",
    RemoteLoaderWaitPolicy.Classify(0) == RemoteLoaderWaitOutcome.Completed &&
    RemoteLoaderWaitPolicy.CanReleaseRemoteArguments(0));
Check("Timeout aborts Host without freeing active DLL path",
    RemoteLoaderWaitPolicy.Classify(258) == RemoteLoaderWaitOutcome.TimedOut &&
    !RemoteLoaderWaitPolicy.CanReleaseRemoteArguments(258));
Check("Wait failure does not prematurely free remote path",
    RemoteLoaderWaitPolicy.Classify(0xffffffff) == RemoteLoaderWaitOutcome.Failed &&
    !RemoteLoaderWaitPolicy.CanReleaseRemoteArguments(0xffffffff));
Check("Small timeout remains bounded", RemoteLoaderWaitPolicy.LoadTimeoutMs == 10000);
// Static source check prevents regression to infinite waits after timeout.
var projectRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../"));
var program=File.ReadAllText(Path.Combine(projectRoot,"Program.cs"));
Check("Remote loader contains no INFINITE WaitForSingleObject",
    !program.Contains("WaitForSingleObject(thread, Infinite)",StringComparison.Ordinal));
Check("Remote loader guard preserves remote memory until thread completed",
    program.Contains("RemoteLoaderWaitPolicy.CanReleaseRemoteArguments",StringComparison.Ordinal));
Console.WriteLine("PASS OSC Adapter bounded loader wait tests");
