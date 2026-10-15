using Sentinel.Api.Configuration;
using Sentinel.Api.DTOs;
using Sentinel.Api.Middleware;
using Sentinel.Api.Models;
using Sentinel.Api.Services;
using Sentinel.Api.Validators;

namespace Sentinel.Api.Tests;

public class AlertRulesTests
{
    private static DetectionFrameDto Frame(bool fire = false, Severity? fireSeverity = null, int humans = 0, int danger = 0, bool smoke = false) =>
        new("cam-1", DateTime.UtcNow, 1280, 720, false, [], [], null, [], fire, fireSeverity, humans, danger, SmokeDetected: smoke);

    [Fact]
    public void Smoke_without_flames_is_medium_but_not_an_emergency()
    {
        Assert.Equal(Severity.Medium, AlertEngine.ClassifyDetection(Frame(smoke: true)));
        Assert.False(AlertEngine.IsEmergency(Frame(smoke: true)));
        Assert.Equal(Severity.High, AlertEngine.ClassifyDetection(Frame(fire: true, fireSeverity: Severity.High, smoke: true)));
    }

    [Fact]
    public void Nothing_alertworthy_returns_null() =>
        Assert.Null(AlertEngine.ClassifyDetection(Frame(humans: 5)));

    [Theory]
    [InlineData(Severity.Low, Severity.Low)]
    [InlineData(Severity.High, Severity.High)]
    [InlineData(null, Severity.Medium)]
    public void Fire_without_people_uses_ai_fire_severity(Severity? fireSeverity, Severity expected) =>
        Assert.Equal(expected, AlertEngine.ClassifyDetection(Frame(fire: true, fireSeverity: fireSeverity)));

    [Fact]
    public void Fire_with_people_in_view_is_at_least_high() =>
        Assert.Equal(Severity.High, AlertEngine.ClassifyDetection(Frame(fire: true, fireSeverity: Severity.Low, humans: 2)));

    [Fact]
    public void Fire_with_humans_in_danger_is_critical() =>
        Assert.Equal(Severity.Critical, AlertEngine.ClassifyDetection(Frame(fire: true, fireSeverity: Severity.Low, humans: 2, danger: 1)));

    [Theory]
    [InlineData(1, Severity.High)]
    [InlineData(3, Severity.Critical)]
    public void Danger_without_fire_scales_with_count(int danger, Severity expected) =>
        Assert.Equal(expected, AlertEngine.ClassifyDetection(Frame(danger: danger)));

    [Fact]
    public void Emergency_when_danger_or_critical_fire()
    {
        Assert.True(AlertEngine.IsEmergency(Frame(danger: 1)));
        Assert.True(AlertEngine.IsEmergency(Frame(fire: true, fireSeverity: Severity.Critical)));
        Assert.False(AlertEngine.IsEmergency(Frame(fire: true, fireSeverity: Severity.High, humans: 3)));
    }

    [Theory]
    [InlineData(10, SensorState.Normal)]
    [InlineData(46, SensorState.Warning)]   // >= 57 * 0.8
    [InlineData(57, SensorState.Alarm)]     // >= threshold
    [InlineData(71.25, SensorState.Critical)] // >= 57 * 1.25
    public void Temperature_states(double value, SensorState expected)
    {
        var sensor = new SensorNode { Type = SensorType.Temperature, Threshold = 57, CurrentValue = value };
        Assert.Equal(expected, SensorRules.StateOf(sensor, new AlertOptions()));
    }

    [Fact]
    public void Validator_derives_counts_from_detections()
    {
        var frame = DetectionPayloadValidator.Validate(new DetectionPayloadDto
        {
            CameraId = "cam-1",
            Detections =
            [
                new DetectionDto { Category = DetectionCategory.Fire, Bbox = [1, 1, 10, 10] },
                new DetectionDto { Category = DetectionCategory.Human, Bbox = [5, 5, 2, 6], AtRisk = true },
                new DetectionDto { Category = DetectionCategory.Human, Bbox = [50, 5, 2, 6] },
            ],
        });
        Assert.True(frame.FireDetected);
        Assert.Equal(2, frame.HumanCount);
        Assert.Equal(1, frame.DangerCount);
    }

    [Fact]
    public void Validator_rejects_bad_bbox_and_heatmap()
    {
        var ex = Assert.Throws<AppValidationException>(() => DetectionPayloadValidator.Validate(new DetectionPayloadDto
        {
            CameraId = "cam 1!",
            Detections = [new DetectionDto { Bbox = [1, 2] }],
            Heatmap = new HeatmapDto { Rows = 2, Cols = 2, Values = [0.1] },
        }));
        Assert.Contains("cameraId", ex.Errors.Keys);
        Assert.Contains("detections[0].bbox", ex.Errors.Keys);
        Assert.Contains("heatmap.values", ex.Errors.Keys);
    }
}
