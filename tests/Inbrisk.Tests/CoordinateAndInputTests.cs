using System.Text.Json;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Native;
using Xunit;

namespace Inbrisk.Tests;

public class CoordinateAndInputTests
{
    [Fact]
    public void TargetSpec_AcceptsCoordinatesWithoutExtraFields()
    {
        var json = """{"x": 41, "y": 36}""";
        var spec = JsonSerializer.Deserialize<InbriskTools.TargetSpec>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(spec);
        Assert.Equal(41, spec.X);
        Assert.Equal(36, spec.Y);
        Assert.True(spec.Extra == null || spec.Extra.Count == 0);
    }

    [Fact]
    public void HardwareScanCodes_MapCorrectly()
    {
        // VK_TAB (0x09) -> Scan Code 0x0F
        var tabScan = NativeMethods.MapVirtualKeyW(0x09, NativeMethods.MAPVK_VK_TO_VSC);
        Assert.Equal(0x0F, (int)tabScan);

        // VK_RETURN (0x0D) -> Scan Code 0x1C
        var enterScan = NativeMethods.MapVirtualKeyW(0x0D, NativeMethods.MAPVK_VK_TO_VSC);
        Assert.Equal(0x1C, (int)enterScan);

        // VK_ESCAPE (0x1B) -> Scan Code 0x01
        var escScan = NativeMethods.MapVirtualKeyW(0x1B, NativeMethods.MAPVK_VK_TO_VSC);
        Assert.Equal(0x01, (int)escScan);

        // VK_SPACE (0x20) -> Scan Code 0x39
        var spaceScan = NativeMethods.MapVirtualKeyW(0x20, NativeMethods.MAPVK_VK_TO_VSC);
        Assert.Equal(0x39, (int)spaceScan);
    }

    [Fact]
    public void PlanStep_ClickWithCoordinates_ValidatesWithoutFrameId()
    {
        var step = new InbriskTools.RunStep(
            Action: "click",
            X: 100,
            Y: 200,
            ObservationId: 8
        );

        var err = InbriskTools.ValidateStep(step);
        Assert.Null(err);
    }

    [Fact]
    public void PlanStep_ClickWithTargetCoordinates_ValidatesSuccessfully()
    {
        var step = new InbriskTools.RunStep(
            Action: "click",
            Target: new InbriskTools.TargetSpec(X: 50, Y: 60)
        );

        var err = InbriskTools.ValidateStep(step);
        Assert.Null(err);
    }
}
