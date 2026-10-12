using Sentinel.Api.DTOs;
using Sentinel.Api.Models;
using Sentinel.Api.Services;
using Sentinel.Api.Validators;

namespace Sentinel.Api.Tests;

public class EvacuationRouterTests
{
    private static FloorPlanElement E(FloorElementType type, double x, double y, double w, double h, string name = "", bool blocked = false) =>
        new() { Type = type, X = x, Y = y, Width = w, Height = h, Name = name, IsBlocked = blocked };

    // 20 x 10 m: one corridor, exit "East" at the right end and exit "West" at the left end.
    private static List<FloorPlanElement> Corridor() =>
    [
        E(FloorElementType.Corridor, 0, 4, 20, 2),
        E(FloorElementType.Exit, 19, 4, 1, 2, "East"),
        E(FloorElementType.Exit, 0, 4, 1, 2, "West"),
    ];

    [Fact]
    public void Finds_nearest_exit_with_plausible_distance()
    {
        var r = EvacuationRouter.FindRoute(20, 10, Corridor(), [], 16, 5);
        Assert.True(r.Found);
        Assert.Equal("East", r.ExitName);
        Assert.InRange(r.DistanceMeters, 2.5, 4); // from x=16 to inside the exit at x>=19
    }

    [Fact]
    public void Avoids_active_danger_zone_by_taking_the_other_exit()
    {
        var danger = E(FloorElementType.DangerZone, 17, 4, 1, 2, "Fire");
        var r = EvacuationRouter.FindRoute(20, 10, [.. Corridor(), danger], [danger], 16, 5);
        Assert.True(r.Found);
        Assert.Equal("West", r.ExitName);
        Assert.DoesNotContain(r.Path, p => p[0] is >= 17 and <= 18);
    }

    [Fact]
    public void Wall_with_blocked_door_makes_exit_unreachable_but_open_door_lets_route_through()
    {
        // Room (0..10) | wall at x=10 with a door | room (10..20) holding the only exit.
        List<FloorPlanElement> Plan(bool blocked) =>
        [
            E(FloorElementType.Room, 0, 0, 10, 10), E(FloorElementType.Room, 10, 0, 10, 10),
            E(FloorElementType.Wall, 9.9, 0, 0.2, 10), E(FloorElementType.Door, 9.8, 4, 0.4, 2, "D1", blocked),
            E(FloorElementType.Exit, 19, 4, 1, 2, "Main"),
        ];

        var closed = EvacuationRouter.FindRoute(20, 10, Plan(true), [], 2, 5);
        Assert.False(closed.Found);
        Assert.Contains("No safe route", closed.Message);

        var open = EvacuationRouter.FindRoute(20, 10, Plan(false), [], 2, 5);
        Assert.True(open.Found);
        // Straight line from (2,5) through the door gap (y 4..6) to the exit; it never crosses the solid wall.
        Assert.All(open.Path, p => Assert.InRange(p[1], 4, 6));
        Assert.InRange(open.DistanceMeters, 17, 18.5);
    }

    [Fact]
    public void Reports_missing_exit_and_unwalkable_start()
    {
        Assert.Contains("no open emergency exit",
            EvacuationRouter.FindRoute(20, 10, [E(FloorElementType.Corridor, 0, 4, 20, 2)], [], 5, 5).Message);
        var outside = EvacuationRouter.FindRoute(20, 10, Corridor(), [], 5, 1); // y=1 is outside the corridor
        Assert.False(outside.Found);
        Assert.Contains("not inside a room", outside.Message);
    }

    [Fact]
    public void Person_inside_danger_zone_can_still_walk_out()
    {
        var danger = E(FloorElementType.DangerZone, 14, 4, 4, 2, "Fire");
        var r = EvacuationRouter.FindRoute(20, 10, [.. Corridor(), danger], [danger], 15, 5);
        Assert.True(r.Found);
        Assert.True(r.StartInDangerZone);
    }

    [Fact]
    public void Backend_marks_person_inside_danger_zone_but_not_inside_safe_zone()
    {
        var frame = DetectionPayloadValidator.Validate(new DetectionPayloadDto
        {
            CameraId = "cam-1",
            Detections =
            [
                new DetectionDto { Category = DetectionCategory.Human, Bbox = [100, 100, 20, 50], TrackId = "p1" },  // feet (110,150) in danger
                new DetectionDto { Category = DetectionCategory.Human, Bbox = [500, 100, 20, 50], TrackId = "p2" },  // feet (510,150) in safe zone
            ],
            DangerZones =
            [
                new DangerZoneDto { Label = "Fire area", Level = ZoneLevel.Danger, Polygon = [[50, 50], [200, 50], [200, 200], [50, 200]] },
                new DangerZoneDto { Label = "Exit B", Level = ZoneLevel.Safe, Polygon = [[450, 50], [600, 50], [600, 200], [450, 200]] },
            ],
            DangerCount = 0, // the producer says nobody is in danger; the backend check must override it
        });

        Assert.Equal(1, frame.DangerCount);
        Assert.True(frame.Detections[0].AtRisk);
        Assert.Equal("Fire area", frame.Detections[0].DangerZone);
        Assert.False(frame.Detections[1].AtRisk);
        Assert.Contains(frame.DangerZones, z => z.Level == ZoneLevel.Safe);
    }
}
