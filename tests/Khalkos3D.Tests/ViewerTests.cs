using System.Numerics;
using Xunit;

namespace Khalkos3D.Tests;

/// <summary>The viewer essentials — orbit, pan, zoom, and the scenery — none of which needs a GPU to
/// be wrong.</summary>
public class ViewerTests
{
    private static BoundingBox Box(float size) =>
        new(new Vector3(-size / 2), new Vector3(size / 2));

    // ---- the controller ---------------------------------------------------------------------

    [Fact]
    public void Panning_moves_what_is_being_looked_at_rather_than_only_the_eye()
    {
        // A pan that moved the eye alone would swing the view around a fixed point, which is an
        // orbit. Both have to move, together, or dragging feels like the model is on a turntable.
        var controller = new OrbitController();
        controller.Frame(Box(10), UpAxis.Z);
        var before = controller.Camera;

        controller.Pan(0.25f, 0f);

        Assert.NotEqual(before.Target, controller.Camera.Target);
        Assert.Equal(before.Position - before.Target, controller.Camera.Position - controller.Camera.Target);
    }

    [Fact]
    public void A_pan_moves_the_same_fraction_of_the_screen_at_any_zoom()
    {
        // Scaled by distance, which is what makes dragging feel like moving the object rather than
        // nudging a camera: unscaled, the same gesture is wild when zoomed out and useless up close.
        var near = new OrbitController();
        near.Frame(Box(10), UpAxis.Z);
        var far = new OrbitController();
        far.Frame(Box(100), UpAxis.Z);

        var nearBefore = near.Camera.Target;
        var farBefore = far.Camera.Target;
        near.Pan(0.5f, 0f);
        far.Pan(0.5f, 0f);

        var nearMoved = Vector3.Distance(near.Camera.Target, nearBefore);
        var farMoved = Vector3.Distance(far.Camera.Target, farBefore);

        // Ten times the model, ten times the movement — so the same fraction of the viewport.
        Assert.Equal(10f, farMoved / nearMoved, 1);
    }

    [Fact]
    public void Zooming_in_and_back_out_returns_to_where_it_started()
    {
        var controller = new OrbitController();
        controller.Frame(Box(10), UpAxis.Z);
        var before = controller.Camera.Distance;

        controller.Zoom(3f);
        Assert.True(controller.Camera.Distance < before);
        controller.Zoom(-3f);

        // Drift here is the thing a user notices after a minute of scrolling and cannot explain.
        Assert.Equal(before, controller.Camera.Distance, 3);
    }

    [Fact]
    public void A_wider_window_does_not_disturb_a_view_the_user_set_up()
    {
        // Re-framing on every resize would undo an orbit each time somebody dragged a window edge.
        // Widening cannot newly clip anything, because a perspective projection is specified
        // vertically — so there is nothing to fix.
        var controller = new OrbitController();
        controller.Frame(Box(10), UpAxis.Z, aspect: 1f);
        controller.Orbit(0.3f, 0.1f);
        var before = controller.Camera;

        controller.Resize(2.5f);

        Assert.Equal(before, controller.Camera);
    }

    [Fact]
    public void A_narrower_window_pulls_back_far_enough_to_keep_the_model()
    {
        // The case that DOES newly clip: portrait has a narrower horizontal field than vertical.
        var bounds = Box(10);
        var controller = new OrbitController();
        controller.Frame(bounds, UpAxis.Z, aspect: 2f);
        var before = controller.Camera.Distance;

        controller.Resize(0.4f);

        Assert.True(controller.Camera.Distance > before,
            "the window got narrower and the camera did not pull back, so the model is cut off");
    }

    [Fact]
    public void Reset_recovers_a_view_that_has_been_lost()
    {
        // Every viewer needs this, because every viewer's user eventually pans the model off screen
        // and has no idea which way to drag to get it back.
        var controller = new OrbitController();
        controller.Frame(Box(10), UpAxis.Z);
        var framed = controller.Camera;

        controller.Pan(20f, 14f);
        controller.Zoom(30f);
        Assert.NotEqual(framed, controller.Camera);

        controller.Reset();
        Assert.Equal(framed, controller.Camera);
    }

    [Fact]
    public void The_controller_takes_its_up_axis_from_the_scene()
    {
        // So a caller cannot forget: an STL is Z-up, and a viewer that assumed otherwise lays such
        // models on their side.
        var scene = Scene.FromMesh(
            MeshWelder.FromTriangleSoup(TestShapes.Box(new Vector3(10, 10, 10)), out _), up: UpAxis.Z);

        var controller = new OrbitController();
        controller.Frame(scene);

        Assert.Equal(UpAxis.Z, controller.Up);
        Assert.Equal(Vector3.UnitZ, controller.Camera.Up);
    }

    // ---- the scenery ------------------------------------------------------------------------

    [Theory]
    [InlineData(UpAxis.Z)]
    [InlineData(UpAxis.Y)]
    public void A_grid_lies_flat_in_the_ground_plane(UpAxis up)
    {
        var grid = Shapes.Grid(50f, 10f, up);

        Assert.Null(grid.Validate());
        Assert.Equal(PrimitiveKind.Lines, grid.Kind);
        foreach (var p in grid.Positions)
        {
            var height = up == UpAxis.Z ? p.Z : p.Y;
            Assert.Equal(0f, height);
        }
    }

    [Fact]
    public void A_platform_can_put_its_origin_at_a_corner_or_in_the_middle()
    {
        // Not cosmetic: an object positioned against the wrong origin appears outside a region it is
        // actually inside, and machine coordinate systems differ on which convention they use.
        var corner = Shapes.Platform(220f, 210f, 10f, UpAxis.Z, centred: false);
        Assert.Equal(new Vector3(0, 0, 0), corner.Bounds.Min);
        Assert.Equal(new Vector3(220, 210, 0), corner.Bounds.Max);

        var centred = Shapes.Platform(220f, 210f, 10f, UpAxis.Z, centred: true);
        Assert.Equal(new Vector3(-110, -105, 0), centred.Bounds.Min);
        Assert.Equal(new Vector3(110, 105, 0), centred.Bounds.Max);
    }

    [Fact]
    public void The_axes_are_red_green_blue_for_x_y_z()
    {
        // The one thing on screen a user can read without being told, because every CAD package and
        // every engine uses this mapping.
        var axes = Shapes.Axes(10f);
        var colors = axes.Colors!;

        Assert.True(colors[1].X > colors[1].Y && colors[1].X > colors[1].Z, "X is not red");
        Assert.True(colors[3].Y > colors[3].X && colors[3].Y > colors[3].Z, "Y is not green");
        Assert.True(colors[5].Z > colors[5].X && colors[5].Z > colors[5].Y, "Z is not blue");
        Assert.Equal(new Vector3(10, 10, 10), axes.Bounds.Max);
    }

    [Theory]
    [InlineData(0.005f)]    // a tiny component
    [InlineData(1f)]
    [InlineData(220f)]      // a work area
    [InlineData(300f)]      // a building
    public void Scenery_sized_to_a_model_uses_a_readable_step(float size)
    {
        // A fixed spacing cannot serve five orders of magnitude. Snapping to a 1-2-5 sequence is what
        // makes the squares countable — 10 and 20 and 50, never 13.7.
        var (grid, _) = Shapes.For(new BoundingBox(Vector3.Zero, new Vector3(size)), UpAxis.Z);
        Assert.Null(grid.Validate());
        Assert.True(grid.Positions.Length > 0);

        // Recover the step from the two closest distinct line positions.
        var xs = grid.Positions.Select(p => p.X).Distinct().OrderBy(v => v).ToArray();
        var step = xs[1] - xs[0];
        var magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(step)));
        var mantissa = step / magnitude;
        Assert.True(MathF.Abs(mantissa - 1f) < 0.01f || MathF.Abs(mantissa - 2f) < 0.01f ||
                    MathF.Abs(mantissa - 5f) < 0.01f,
            $"a model of {size} produced a grid step of {step}, whose mantissa {mantissa} is not 1, 2 or 5");
    }

    [Fact]
    public void Scenery_is_unlit_so_a_grid_line_is_not_shaded_by_a_lamp()
    {
        // A line has no meaningful normal, so lighting one makes it dim on one side of the model and
        // bright on the other — which reads as a rendering fault.
        Assert.True(Shapes.LineMaterial.Unlit);
        Assert.False(Material.Default.Unlit);
    }

    [Fact]
    public void Absurd_scenery_parameters_produce_something_usable_rather_than_millions_of_lines()
    {
        // A grid asked for a spacing of zero would otherwise loop until the upload stalls, with no
        // error to explain the freeze.
        var zero = Shapes.Grid(100f, 0f, UpAxis.Z);
        Assert.Null(zero.Validate());
        Assert.True(zero.Positions.Length < 10_000);

        var tiny = Shapes.Grid(100f, 1e-9f, UpAxis.Z);
        Assert.True(tiny.Positions.Length < 10_000, $"{tiny.Positions.Length} vertices is a hung upload");

        Assert.Null(Shapes.Grid(float.NaN, float.NaN, UpAxis.Z).Validate());
        Assert.Null(Shapes.Platform(0f, -5f, 0f).Validate());
    }
}
