using NISS.Monitor.Core.Engines;

namespace NISS.Monitor.Tests;

/// <summary>
/// The pure rule that decides whether a service stop is a benign OS shutdown or a deliberate stop worth a tamper
/// alert, and who was signed in. Keeping it pure lets us pin the false-positive policy without a real service.
/// </summary>
public sealed class StopClassifierTests
{
    [Fact]
    public void Os_shutdown_is_benign_even_with_a_user_present()
    {
        var r = StopClassifier.Classify(isShuttingDown: true, consoleUser: "CONTOSO\\jdoe");
        Assert.Equal(StopClassifier.Shutdown, r.Reason);
        Assert.Null(r.InteractiveUser); // a normal shutdown must never raise a tamper alert
    }

    [Fact]
    public void Deliberate_stop_while_a_user_is_signed_in_is_attributed()
    {
        var r = StopClassifier.Classify(isShuttingDown: false, consoleUser: "CONTOSO\\jdoe");
        Assert.Equal(StopClassifier.ServiceStop, r.Reason);
        Assert.Equal("CONTOSO\\jdoe", r.InteractiveUser); // tamper: record who was signed in
    }

    [Fact]
    public void Stop_with_no_console_user_is_not_attributed()
    {
        var r = StopClassifier.Classify(isShuttingDown: false, consoleUser: null);
        Assert.Equal(StopClassifier.ServiceStop, r.Reason);
        Assert.Null(r.InteractiveUser); // nobody signed in (e.g. remote mgmt / our own tooling) -> no attribution
    }

    [Fact]
    public void Blank_console_user_is_treated_as_no_user()
    {
        Assert.Null(StopClassifier.Classify(isShuttingDown: false, consoleUser: "   ").InteractiveUser);
    }
}
