namespace SampleTarget.Tests;

/// <summary>
/// Pass unless named in <c>failing_tests</c> (or <c>failing_tests</c> contains <c>"*"</c>).
/// </summary>
public class OutcomeTests
{
    [Fact]
    public void Alpha() => Check(nameof(Alpha));

    [Fact]
    public void Beta() => Check(nameof(Beta));

    [Fact]
    public void Gamma() => Check(nameof(Gamma));

    private static void Check(string name)
    {
        if (SandboxSwitches.Current.ShouldFail(name))
        {
            Assert.Fail($"sandbox.json failing_tests includes {name}");
        }
    }
}
