using System.IO;
using FluentAssertions;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using ODriveStudio.App.Services;
using ODriveStudio.App.ViewModels;
using Xunit;

namespace ODriveStudio.App.Tests;

internal sealed class FakeDialogs : IUserDialogs
{
    public bool Answer { get; set; } = true;
    public List<string> Questions { get; } = [];
    public string? SavePath { get; set; }

    public bool Confirm(string title, string message)
    {
        Questions.Add(message);
        return Answer;
    }

    public string? PickSaveFile(string title, string defaultFileName, string filter) => SavePath;
}

/// <summary>Une carte simulée connectée, avec les services dont les vues-modèles ont besoin.</summary>
internal sealed class Bench
{
    public SimulatedODrive Board { get; } = SimulatedODrive.CreateReference();
    public WriteJournal Journal { get; } = new();
    public SafetyGuard Guard { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public List<string> Status { get; } = [];
    public DeviceSession Session { get; private set; } = null!;

    public static async Task<Bench> CreateAsync()
    {
        var bench = new Bench();
        ODriveDevice device = await ODriveDevice.ConnectAsync(new SimulatedTransport(bench.Board),
            new ODriveDeviceOptions { Journal = bench.Journal });
        bench.Session = new DeviceSession(device, bench.Guard, bench.Dialogs, bench.Status.Add);
        return bench;
    }
}

public class DashboardViewModelTests
{
    [Fact]
    public async Task Refresh_shows_bus_and_axis_values_with_their_units()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Board.Set("vbus_voltage", 36.25f);
        bench.Board.Set("ibus", 1.5f);
        bench.Board.Set("axis1.encoder.vel_estimate", 2.5f);
        bench.Board.Set("axis1.controller.config.control_mode", (byte)ControlMode.VelocityControl);
        bench.Board.Set("axis1.controller.input_vel", 3f);
        bench.Board.Set("axis1.motor.fet_thermistor.temperature", 41.26f);
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.RefreshAsync();

        dashboard.BusVoltage.Should().Be("36.25 V");
        dashboard.BusCurrent.Should().Be("1.50 A");
        dashboard.BrakeResistor.Should().Be("désactivée");
        AxisDashboardViewModel axis1 = dashboard.Axes[1];
        axis1.Name.Should().Be("axis1");
        axis1.CurrentState.Should().Be("IDLE (1)");
        axis1.ControlMode.Should().Be("VELOCITY_CONTROL (2)");
        axis1.Setpoint.Should().Be("3.000 tours/s");
        axis1.Velocity.Should().Be("2.500 tours/s");
        axis1.FetTemperature.Should().Be("41.3 °C");
        axis1.MotorTemperature.Should().Be("thermistance non configurée");
        dashboard.Axes[0].Setpoint.Should().EndWith("tours"); // défaut : POSITION_CONTROL
    }

    [Fact]
    public async Task Refresh_lists_decoded_errors_and_clear_removes_them()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Board.Set("axis0.encoder.error", (ushort)0x10);
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.RefreshAsync();

        dashboard.HasErrors.Should().BeTrue();
        dashboard.Errors.Should().ContainSingle().Which.Name.Should().Be("ENCODER_ERROR_ILLEGAL_HALL_STATE");

        await dashboard.ClearErrorsCommand.ExecuteAsync(null);
        await dashboard.RefreshAsync();

        dashboard.HasErrors.Should().BeFalse();
        dashboard.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Closed_loop_asks_for_confirmation_then_arms_the_axis()
    {
        Bench bench = await Bench.CreateAsync();
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.Axes[0].ClosedLoopCommand.ExecuteAsync(null);
        await dashboard.RefreshAsync();

        bench.Dialogs.Questions.Should().ContainSingle();
        dashboard.Axes[0].CurrentState.Should().Be("CLOSED_LOOP_CONTROL (8)");
        dashboard.Axes[0].IsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task Closed_loop_is_not_requested_when_the_user_declines()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Dialogs.Answer = false;
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.Axes[0].ClosedLoopCommand.ExecuteAsync(null);

        bench.Board.Get("axis0.current_state").Should().Be((byte)AxisState.Idle);
        bench.Journal.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Closed_loop_is_refused_with_the_reason_when_the_board_has_errors()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Board.Set("axis0.motor.error", 0x1ul);
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.Axes[1].ClosedLoopCommand.ExecuteAsync(null);

        bench.Board.Get("axis1.current_state").Should().Be((byte)AxisState.Idle);
        bench.Dialogs.Questions.Should().BeEmpty();
        bench.Status.Should().ContainSingle().Which.Should().Contain("MOTOR_ERROR_PHASE_RESISTANCE_OUT_OF_RANGE");
    }

    [Fact]
    public async Task Idle_is_sent_without_any_confirmation()
    {
        Bench bench = await Bench.CreateAsync();
        var dashboard = new DashboardViewModel(bench.Session);
        await dashboard.Axes[0].ClosedLoopCommand.ExecuteAsync(null);
        bench.Dialogs.Questions.Clear();

        await dashboard.Axes[0].IdleCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Should().BeEmpty();
        bench.Board.Get("axis0.current_state").Should().Be((byte)AxisState.Idle);
    }

    [Fact]
    public async Task Save_is_refused_while_an_axis_is_in_closed_loop()
    {
        Bench bench = await Bench.CreateAsync();
        var dashboard = new DashboardViewModel(bench.Session);
        await dashboard.Axes[0].ClosedLoopCommand.ExecuteAsync(null);
        bench.Dialogs.Questions.Clear();
        bench.Status.Clear();

        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);

        bench.Session.Device.IsConnected.Should().BeTrue();
        bench.Dialogs.Questions.Should().BeEmpty();
        bench.Status.Should().ContainSingle().Which.Should().Contain("axis0");
    }

    [Fact]
    public async Task Save_asks_for_confirmation_then_the_board_reboots_with_the_saved_values()
    {
        Bench bench = await Bench.CreateAsync();
        await bench.Session.Device.WriteAsync("axis0.controller.config.vel_limit", 7f);
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.SaveConfigurationCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Should().ContainSingle();
        bench.Session.Device.IsConnected.Should().BeFalse();
        bench.Board.Get("axis0.controller.config.vel_limit").Should().Be(7f);
    }

    [Fact]
    public async Task Reboot_is_cancelled_when_the_user_declines()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Dialogs.Answer = false;
        var dashboard = new DashboardViewModel(bench.Session);

        await dashboard.RebootCommand.ExecuteAsync(null);

        bench.Session.Device.IsConnected.Should().BeTrue();
    }
}

public class ExplorerViewModelTests
{
    [Fact]
    public async Task Tree_mirrors_the_board_with_generated_documentation()
    {
        Bench bench = await Bench.CreateAsync();
        var explorer = new ExplorerViewModel(bench.Session);

        explorer.Roots[0].Name.Should().Be("error");
        ExplorerNodeViewModel velLimit = explorer.Find("axis0.controller.config.vel_limit")!;
        velLimit.IsProperty.Should().BeTrue();
        velLimit.IsWritable.Should().BeTrue();
        velLimit.TypeName.Should().Be("float");
        velLimit.Unit.Should().Be("turn/s");
        velLimit.Parent!.Path.Should().Be("axis0.controller.config");

        ExplorerNodeViewModel state = explorer.Find("axis0.requested_state")!;
        state.HasChoices.Should().BeTrue();
        state.Choices.Should().Contain("CLOSED_LOOP_CONTROL (8)");
        state.Documentation.Should().NotBeEmpty();

        ExplorerNodeViewModel function = explorer.Find("get_adc_voltage")!;
        function.IsFunction.Should().BeTrue();
        function.Arguments.Should().ContainSingle().Which.Name.Should().Be("gpio");
    }

    [Fact]
    public async Task Refresh_reads_only_the_properties_whose_branch_is_expanded()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Board.Set("vbus_voltage", 30f);
        bench.Board.Set("axis0.controller.config.vel_limit", 4f);
        var explorer = new ExplorerViewModel(bench.Session);

        await explorer.RefreshVisibleAsync();

        explorer.Find("vbus_voltage")!.CurrentValue.Should().Be("30");
        explorer.Find("axis0.controller.config.vel_limit")!.CurrentValue.Should().BeEmpty();

        explorer.Find("axis0")!.IsExpanded = true;
        explorer.Find("axis0.controller")!.IsExpanded = true;
        explorer.Find("axis0.controller.config")!.IsExpanded = true;
        for (int i = 0; i < 5; i++)
        {
            await explorer.RefreshVisibleAsync();
        }

        explorer.Find("axis0.controller.config.vel_limit")!.CurrentValue.Should().Be("4");
        explorer.Find("axis0.current_state")!.CurrentValue.Should().Be("IDLE (1)");
    }

    [Fact]
    public async Task Search_keeps_matching_nodes_and_expands_their_branches()
    {
        Bench bench = await Bench.CreateAsync();
        var explorer = new ExplorerViewModel(bench.Session);

        explorer.SearchText = "pole_pairs";

        explorer.Find("axis0.motor.config.pole_pairs")!.IsVisible.Should().BeTrue();
        explorer.Find("axis0.motor.config")!.IsExpanded.Should().BeTrue();
        explorer.Find("axis0")!.IsExpanded.Should().BeTrue();
        explorer.Find("axis0.motor.config.current_lim")!.IsVisible.Should().BeFalse();
        explorer.Find("vbus_voltage")!.IsVisible.Should().BeFalse();
        explorer.MatchSummary.Should().Be("2 résultat(s)");

        explorer.SearchText = "";

        explorer.Find("vbus_voltage")!.IsVisible.Should().BeTrue();
        explorer.MatchSummary.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_also_looks_into_the_documentation()
    {
        Bench bench = await Bench.CreateAsync();
        var explorer = new ExplorerViewModel(bench.Session);

        explorer.SearchText = "watchdog timeouts";

        explorer.Find("axis0.watchdog_feed")!.IsVisible.Should().BeTrue();
    }

    [Fact]
    public async Task Apply_writes_the_typed_value_and_journals_it()
    {
        Bench bench = await Bench.CreateAsync();
        var explorer = new ExplorerViewModel(bench.Session);
        ExplorerNodeViewModel node = explorer.Find("axis0.controller.config.vel_limit")!;

        node.EditText = "7,5";
        await node.ApplyCommand.ExecuteAsync(null);

        bench.Board.Get("axis0.controller.config.vel_limit").Should().Be(7.5f);
        node.CurrentValue.Should().Be("7.5");
        node.Message.Should().BeEmpty();
        bench.Journal.Entries.Should().ContainSingle().Which.OldValue.Should().Be(2f);
    }

    [Fact]
    public async Task Apply_reports_invalid_input_without_writing()
    {
        Bench bench = await Bench.CreateAsync();
        var explorer = new ExplorerViewModel(bench.Session);
        ExplorerNodeViewModel node = explorer.Find("axis0.motor.config.pole_pairs")!;

        node.EditText = "quinze";
        await node.ApplyCommand.ExecuteAsync(null);

        node.Message.Should().Contain("quinze");
        bench.Board.Get("axis0.motor.config.pole_pairs").Should().Be(7);
        bench.Journal.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_on_requested_state_goes_through_the_safety_guard()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Dialogs.Answer = false;
        var explorer = new ExplorerViewModel(bench.Session);
        ExplorerNodeViewModel node = explorer.Find("axis0.requested_state")!;

        node.EditText = "CLOSED_LOOP_CONTROL (8)";
        await node.ApplyCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Should().ContainSingle();
        bench.Board.Get("axis0.current_state").Should().Be((byte)AxisState.Idle);

        bench.Dialogs.Answer = true;
        await node.ApplyCommand.ExecuteAsync(null);

        bench.Board.Get("axis0.current_state").Should().Be((byte)AxisState.ClosedLoopControl);
    }

    [Fact]
    public async Task Call_passes_arguments_and_shows_named_outputs()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Board.OnCall("get_adc_voltage", call => call.SetOutput("voltage", (uint)call.Input("gpio") * 0.5f));
        var explorer = new ExplorerViewModel(bench.Session);
        ExplorerNodeViewModel node = explorer.Find("get_adc_voltage")!;

        node.Arguments[0].Text = "3";
        await node.CallCommand.ExecuteAsync(null);

        node.Message.Should().Be("voltage = 1.5");
    }

    [Fact]
    public async Task Call_of_reboot_requires_confirmation()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Dialogs.Answer = false;
        var explorer = new ExplorerViewModel(bench.Session);

        await explorer.Find("reboot")!.CallCommand.ExecuteAsync(null);

        bench.Dialogs.Questions.Should().ContainSingle();
        bench.Session.Device.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Selecting_a_property_prefills_the_edit_box_with_its_value()
    {
        Bench bench = await Bench.CreateAsync();
        var explorer = new ExplorerViewModel(bench.Session);
        await explorer.RefreshVisibleAsync();
        ExplorerNodeViewModel node = explorer.Find("vbus_voltage")!;

        node.IsSelected = true;

        explorer.SelectedNode.Should().BeSameAs(node);
        node.EditText.Should().Be("24");
    }
}

public class MainViewModelTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        condition().Should().BeTrue("la condition attendue n'est pas arrivée en 2 s");
    }

    private static (MainViewModel Main, DeviceManager Manager, SimulatedODrive Board, WriteJournal Journal, FakeDialogs Dialogs) Create(
        Func<string?>? driverHint = null)
    {
        var board = SimulatedODrive.CreateReference();
        var journal = new WriteJournal();
        var dialogs = new FakeDialogs();
        var manager = new DeviceManager([new SimulatedTransportSource(board)], new ODriveDeviceOptions { Journal = journal });
        var main = new MainViewModel(manager, journal, new SafetyGuard(), dialogs, driverHint)
        {
            PollingPeriod = TimeSpan.FromMilliseconds(10),
        };
        return (main, manager, board, journal, dialogs);
    }

    [Fact]
    public async Task Connected_board_appears_is_selected_and_shows_live_values()
    {
        var (main, manager, board, _, _) = Create();
        main.HasDevice.Should().BeFalse();
        main.ConnectionHint.Should().Contain("Aucune carte");
        board.Set("vbus_voltage", 31f);

        await manager.ScanAsync();

        main.Devices.Should().ContainSingle();
        main.SelectedDevice.Should().BeSameAs(main.Devices[0]);
        main.ConnectionHint.Should().BeEmpty();
        await WaitUntilAsync(() => main.SelectedDevice!.Dashboard.BusVoltage == "31.00 V");
        main.SelectedDevice!.Title.Should().Contain("206538845748").And.Contain("v3.6-56V").And.Contain("0.5.6");
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Board_without_valid_otp_is_flagged_as_unofficial()
    {
        var (main, manager, board, _, _) = Create();
        board.Set("otp_valid", false);

        await manager.ScanAsync();

        await WaitUntilAsync(() => main.SelectedDevice!.Title.Length > 0);
        main.SelectedDevice!.IsGenuine.Should().BeFalse();
        main.SelectedDevice.Title.Should().Contain("carte non officielle");
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Emergency_stop_idles_every_axis_and_reports_it()
    {
        var (main, manager, board, _, _) = Create();
        await manager.ScanAsync();
        await main.SelectedDevice!.Session.Odrv.Axis1.RequestedState.SetAsync(AxisState.ClosedLoopControl);

        await main.EmergencyStopCommand.ExecuteAsync(null);

        board.Get("axis1.current_state").Should().Be((byte)AxisState.Idle);
        main.StatusMessage.Should().StartWith("ARRÊT D'URGENCE").And.Contain("1 carte");
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Rebooted_board_disappears_then_comes_back_on_the_next_scan()
    {
        var (main, manager, _, _, _) = Create();
        await manager.ScanAsync();
        DeviceViewModel before = main.SelectedDevice!;

        await before.Device.RebootAsync();

        main.Devices.Should().BeEmpty();
        main.SelectedDevice.Should().BeNull();
        main.StatusMessage.Should().Contain("déconnectée");

        await manager.ScanAsync();

        main.Devices.Should().ContainSingle();
        main.SelectedDevice.Should().NotBeNull().And.NotBeSameAs(before);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Journal_lists_writes_with_decoded_values_and_can_be_exported()
    {
        var (main, manager, _, _, dialogs) = Create();
        await manager.ScanAsync();
        ODriveDevice device = main.SelectedDevice!.Device;

        await device.WriteAsync("axis0.controller.config.control_mode", ControlMode.VelocityControl);
        await device.CallAsync("clear_errors", []);

        main.Journal.Should().HaveCount(2);
        main.Journal[0].Should().BeEquivalentTo(new
        {
            Kind = "écriture",
            Path = "axis0.controller.config.control_mode",
            OldValue = "POSITION_CONTROL (3)",
            NewValue = "VELOCITY_CONTROL (2)",
        });
        main.Journal[1].Path.Should().Be("clear_errors()");

        dialogs.SavePath = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.csv");
        try
        {
            main.ExportJournalCommand.Execute(null);

            File.ReadAllLines(dialogs.SavePath).Should().HaveCount(3);
        }
        finally
        {
            File.Delete(dialogs.SavePath);
        }
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Explorer_values_are_refreshed_only_while_its_tab_is_shown()
    {
        var (main, manager, _, _, _) = Create();
        await manager.ScanAsync();
        DeviceViewModel device = main.SelectedDevice!;

        device.ExplorerActive.Should().BeFalse();
        main.SelectedTabIndex = 1;
        device.ExplorerActive.Should().BeTrue();

        await WaitUntilAsync(() => device.Explorer.Find("vbus_voltage")!.CurrentValue == "24");
        await manager.DisposeAsync();
    }

    [Fact]
    public void Hint_explains_the_driver_problem_when_a_board_is_seen_without_winusb()
    {
        var (main, _, _, _, _) = Create(() => "Pilote : utiliser Zadig.");

        main.UpdateConnectionHint();

        main.ConnectionHint.Should().Contain("Zadig");
    }
}
