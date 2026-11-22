using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoundStudio.App.Services;
using SoundStudio.App.ViewModels;
using SoundStudio.Core;

namespace SoundStudio.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Sound Studio – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var viewModel = new MainViewModel(AppSettings.Load());
        var window = new MainWindow(viewModel);
        if (!e.Args.Contains("--screenshot"))
            viewModel.Dialogs = window;
        else
        {
            viewModel.SuppressSync = true;
            viewModel.SuppressAutoPlay = !e.Args.Contains("--allow-play");
        }
        MainWindow = window;
        window.Show();

        await viewModel.InitializeAsync();

        var screenshotIndex = Array.IndexOf(e.Args, "--screenshot");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            await TakeScreenshotAsync(window, viewModel, e.Args[screenshotIndex + 1], e.Args);
    }

    /// <summary>
    /// Developer aid: <c>--screenshot out.png [--search text] [--modified] [--node "Title"] [--select N]</c>
    /// renders the window to a PNG and exits. Used to check the UI without clicking through it.
    /// </summary>
    private async Task TakeScreenshotAsync(MainWindow window, MainViewModel vm, string path, string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Arg("--node") is { } nodeTitle)
        {
            var node = vm.AllNodes().FirstOrDefault(n => n.Title == nodeTitle);
            if (node != null)
            {
                for (var p = vm.FindParent(node); p != null; p = vm.FindParent(p))
                    p.IsExpanded = true;
                node.IsSelected = true;
                vm.SelectedNode = node;
            }
        }
        if (args.Contains("--modified"))
            vm.ModifiedOnly = true;
        if (Arg("--search") is { } search)
            vm.SearchText = search;

        await Task.Delay(800); // debounce + background filter

        // --recorder-demo: show the recorder with a synthetic take (never opens the microphone).
        if (args.Contains("--recorder-demo") && vm.VisibleRows.FirstOrDefault(r => r.CanReplace) is { } demoRow)
        {
            var recorder = vm.CreateRecorder(demoRow, demo: true);
            recorder.Devices.Add(new SoundStudio.Audio.MicDevice("demo", "Microphone (USB Audio Device)", true));
            recorder.SelectedDevice = recorder.Devices[0];
            if (args.Contains("--ready"))
                recorder.SelectedBacking = recorder.BackingTracks[^1]; // shows "While recording, play"
            else
                recorder.LoadTake(DemoTake());
            if (args.Contains("--too-long"))
                recorder.ResetTrimCommand.Execute(null);
            recorder.Level = 0.62;
            var recorderWindow = new RecorderWindow(recorder) { Owner = window };
            recorderWindow.Show();
            await Task.Delay(900);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            SaveVisual(recorderWindow.RootGrid, path);
            if (args.Contains("--use"))
            {
                vm.Dialogs = new ScriptedDialogs(null, Path.ChangeExtension(path, ".log"));
                vm.SelectedRow = demoRow;
                await recorder.UseAsReplacementCommand.ExecuteAsync(null);
                await Task.Delay(1500);
                File.AppendAllText(Path.ChangeExtension(path, ".log"),
                    $"STATE pending={vm.PendingCount} source={demoRow.Pending?.SourceFileName} dur={demoRow.Pending?.DurationText}{Environment.NewLine}");
                SaveVisual(window.RootGrid, Path.ChangeExtension(path, ".main.png"));
            }
            else
            {
                recorderWindow.Close();
            }
            Shutdown();
            return;
        }
        if (Arg("--select") is { } sel && int.TryParse(sel, out var index) && index < vm.VisibleRows.Count)
            vm.SelectedRow = vm.VisibleRows[index];

        await Task.Delay(1200); // let the player decode the selected sound
        if (args.Contains("--original"))
            vm.Player.IsOriginalSource = true;
        if (Arg("--seek") is { } seek && double.TryParse(seek, System.Globalization.CultureInfo.InvariantCulture, out var fraction))
            vm.Player.SeekCommand.Execute(fraction);

        if (args.Contains("--play"))
        {
            vm.Player.PlayFromStart();
            await Task.Delay(700);
            File.AppendAllText(Path.ChangeExtension(path, ".log"), $"PLAYING={vm.Player.IsPlaying} after 0.7s pos={vm.Player.PositionText}{Environment.NewLine}");
            vm.Player.PlayPause();
        }
        File.AppendAllText(Path.ChangeExtension(path, ".log"),
            $"PLAYER loaded={vm.Player.IsLoaded} err={vm.Player.ErrorText} pos={vm.Player.PositionText} dur={vm.Player.DurationText} progress={vm.Player.Progress:0.00} peaks={vm.Player.Peaks.Length} max={(vm.Player.Peaks.Length > 0 ? vm.Player.Peaks.Max() : 0):0.000}{Environment.NewLine}");

        // Scripted actions, comma-separated and run in order, e.g. --run stage-folder,apply --pick <path>.
        if (Arg("--run") is { } actions)
        {
            var log = new ScriptedDialogs(Arg("--pick"), Path.ChangeExtension(path, ".log"));
            vm.Dialogs = log;
            foreach (var action in actions.Split(','))
            {
                switch (action)
                {
                    case "revert-selected": await vm.RevertSelectedCommand.ExecuteAsync(null); break;
                    case "revert-visible": await vm.RevertVisibleCommand.ExecuteAsync(null); break;
                    case "import-originals": await vm.ImportOriginalsCommand.ExecuteAsync(null); break;
                    case "stage-file": await vm.ReplaceSelectedCommand.ExecuteAsync(null); break;
                    case "stage-folder": await vm.ReplaceFromFolderCommand.ExecuteAsync(null); break;
                    case "apply": await vm.ApplyPendingCommand.ExecuteAsync(null); break;
                    case "review": vm.ReviewPendingCommand.Execute(null); break;
                    case "select-first": await Task.Delay(400); if (vm.VisibleRows.Count > 0) vm.SelectedRow = vm.VisibleRows[0]; break;
                    case "host":
                        await vm.Sync.StartHostingCommand.ExecuteAsync(null);
                        File.WriteAllText(Path.ChangeExtension(path, ".invite"), vm.Sync.InviteCode ?? "");
                        break;
                    case "join":
                        vm.Sync.JoinCodeText = File.ReadAllText(Arg("--invite")!);
                        await vm.Sync.JoinCommand.ExecuteAsync(null);
                        break;
                    case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(Arg("--wait") ?? "5"))); break;
                    case "volume":
                        if (vm.SelectedRow is { } vr)
                            await vm.SetVolumeAsync(vr, double.Parse(Arg("--db") ?? "-6", System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case "wait-file":
                        for (var i = 0; i < 600 && !File.Exists(Arg("--wait-file")!); i++) await Task.Delay(200);
                        break;
                    case "touch": File.WriteAllText(Arg("--touch")!, "done"); break;
                    case "get-all": vm.GetAllShownCommand.Execute(null); break;
                    case "layers" when vm.SelectedRow is { } lr:
                    {
                        // Opens the layer editor, adds --layer-file files (2nd one quieter and delayed), screenshots, stages.
                        var lvm = vm.CreateLayers(lr);
                        var lw = new LayersWindow(lvm, null) { Owner = window };
                        lw.Show();
                        for (var i = 0; i < 50 && lvm.Layers.Count == 0; i++) await Task.Delay(100);
                        if (Arg("--layer-file") is { } lf)
                            await lvm.AddFilesAsync(lf.Split('|'));
                        if (lvm.Layers.Count > 1) { lvm.Layers[1].GainDb = -6; lvm.Layers[1].OffsetSeconds = 0.3; }
                        if (lvm.Layers.Count > 2) { lvm.Layers[2].OffsetSeconds = 0.62; lvm.Layers[2].TrimEnd = Math.Max(0, lvm.Layers[2].DurationSeconds * 0.4); lvm.Layers[2].FadeIn = 0.15; lvm.Layers[2].FadeOut = 0.3; }
                        if (lvm.Layers.Count > 3) lvm.Layers[3].Muted = true;
                        lvm.MasterGainDb = 3;
                        await Task.Delay(1200);
                        if (args.Contains("--zoom-original")) lvm.ZoomToOriginalCommand.Execute(null);
                        lvm.PlayFrom(0.9);
                        await Task.Delay(300);
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        SaveVisual(lw.RootGrid, Path.ChangeExtension(path, ".layers.png"));
                        File.AppendAllText(Path.ChangeExtension(path, ".log"),
                            $"LAYERS count={lvm.Layers.Count} mix={lvm.MixDurationText} note='{lvm.MixNote}' warn='{lvm.LengthWarning}' err='{lvm.ErrorText}'{Environment.NewLine}");
                        await lvm.UseAsReplacementCommand.ExecuteAsync(null);
                        break;
                    }
                    case "external-edit" when vm.SelectedRow is { } er:
                    {
                        // Opens the sound in the external editor, then "exports" --layer-file over it like the editor would.
                        await vm.EditExternallyCommand.ExecuteAsync(null);
                        var edits = Directory.GetFiles(SoundStudio.Core.AppPaths.EditsFolder, "*.wav", SearchOption.AllDirectories);
                        await Task.Delay(1500);
                        foreach (var f in edits)
                            File.Copy(Arg("--layer-file")!, f, overwrite: true);
                        await Task.Delay(4000);
                        File.AppendAllText(Path.ChangeExtension(path, ".log"),
                            $"EXTERNAL editing='{er.EditingIn}' files={edits.Length} pending={er.Pending?.SourceFileName}{Environment.NewLine}");
                        break;
                    }
                    case "select-available":
                        if (vm.Tree.FirstOrDefault(n => n.Kind == ViewModels.LibraryNodeKind.Available) is { } an) vm.SelectedNode = an;
                        break;
                }
                await Task.Delay(800);
            }
            File.AppendAllText(Path.ChangeExtension(path, ".log"),
                $"STATE pending={vm.PendingCount} tooLong={vm.PendingTooLongCount} summary='{vm.SummaryText}' sync='{vm.Sync.StatusText}' chip='{vm.Sync.ChipText}' peers={vm.Sync.Peers.Count} diag={vm.Sync.Diagnostics}{Environment.NewLine}");
            if (args.Contains("--sync-window"))
            {
                var sw = new SyncWindow(vm.Sync) { Owner = window };
                sw.Show();
                await Task.Delay(700);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                SaveVisual(sw.RootGrid, Path.ChangeExtension(path, ".sync.png"));
                sw.Close();
            }
        }

        await Task.Delay(500);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        SaveVisual(window.RootGrid, path);
        Shutdown();
    }

    /// <summary>0.6 s silence, 1.4 s of a voice-like warble, 0.9 s silence.</summary>
    private static SoundStudio.Audio.DecodedAudio DemoTake()
    {
        const int rate = 48000;
        var frames = (int)(rate * 2.9);
        var pcm = new byte[frames * 2];
        var rng = new Random(5);
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / rate;
            double v = rng.NextDouble() * 0.004 - 0.002;
            if (t is > 0.6 and < 2.0)
            {
                var env = Math.Sin(Math.PI * (t - 0.6) / 1.4) * (0.6 + 0.4 * Math.Sin(t * 18));
                v += env * 0.5 * (Math.Sin(2 * Math.PI * 180 * t) + 0.4 * Math.Sin(2 * Math.PI * 360 * t + 1));
            }
            var s = (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)s;
            pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        return new SoundStudio.Audio.DecodedAudio(pcm, rate, 1);
    }

    private void SaveVisual(FrameworkElement root, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(root);
        var width = root.ActualWidth;
        var height = root.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black;
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var fs = File.Create(path))
            encoder.Save(fs);
    }

}
