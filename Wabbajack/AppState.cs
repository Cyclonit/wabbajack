﻿using Syroot.Windows.IO;
using System;
using ReactiveUI;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wabbajack.Common;
using Wabbajack.Downloaders;
using Wabbajack.NexusApi;
using Wabbajack.UI;
using DynamicData;
using DynamicData.Binding;
using System.Reactive;

namespace Wabbajack
{
    public enum TaskMode { INSTALLING, BUILDING }
    public class AppState : ViewModel, IDataErrorInfo
    {
        public const bool GcCollect = true;

        private SlideShow _slideShow;

        private string _mo2Folder;

        private readonly BitmapImage _wabbajackLogo = UIUtils.BitmapImageFromResource("Wabbajack.UI.banner.png");
        public readonly BitmapImage _noneImage = UIUtils.BitmapImageFromResource("Wabbajack.UI.none.jpg");

        private readonly Subject<CPUStatus> _statusSubject = new Subject<CPUStatus>();
        public ObservableCollectionExtended<CPUStatus> Status { get; } = new ObservableCollectionExtended<CPUStatus>();

        private ModList _ModList;
        public ModList ModList { get => _ModList; private set => this.RaiseAndSetIfChanged(ref _ModList, value); }

        private string _ModListPath;
        public string ModListPath { get => _ModListPath; private set => this.RaiseAndSetIfChanged(ref _ModListPath, value); }

        private TaskMode _Mode;
        public TaskMode Mode { get => _Mode; private set => this.RaiseAndSetIfChanged(ref _Mode, value); }

        private string _ModListName;
        public string ModListName { get => _ModListName; set => this.RaiseAndSetIfChanged(ref _ModListName, value); }

        private bool _EnableSlideShow = true;
        public bool EnableSlideShow { get => _EnableSlideShow; set => this.RaiseAndSetIfChanged(ref _EnableSlideShow, value); }

        private BitmapImage _SplashScreenImage;
        public BitmapImage SplashScreenImage { get => _SplashScreenImage; set => this.RaiseAndSetIfChanged(ref _SplashScreenImage, value); }

        private BitmapImage _NextIcon = UIUtils.BitmapImageFromResource("Wabbajack.UI.Icons.next.png");
        public BitmapImage NextIcon { get => _NextIcon; set => this.RaiseAndSetIfChanged(ref _NextIcon, value); }

        private bool _UIReady;
        public bool UIReady { get => _UIReady; set => this.RaiseAndSetIfChanged(ref _UIReady, value); }

        private string _HTMLReport;
        public string HTMLReport { get => _HTMLReport; set => this.RaiseAndSetIfChanged(ref _HTMLReport, value); }

        private bool _Installing;
        public bool Installing { get => _Installing; set => this.RaiseAndSetIfChanged(ref _Installing, value); }

        // Command properties
        public IReactiveCommand ChangePathCommand { get; }
        public IReactiveCommand ChangeDownloadPathCommand { get; }
        public IReactiveCommand BeginCommand { get; }
        public IReactiveCommand ShowReportCommand { get; }
        public IReactiveCommand VisitNexusSiteCommand { get; }
        public IReactiveCommand OpenReadmeCommand { get; }
        public IReactiveCommand OpenModListPropertiesCommand { get; }
        public IReactiveCommand SlideShowNextItemCommand { get; } = ReactiveCommand.Create(() => { });

        public AppState(TaskMode mode)
        {
            if (Path.GetDirectoryName(Assembly.GetEntryAssembly().Location.ToLower()) == KnownFolders.Downloads.Path.ToLower())
            {
                MessageBox.Show(
                    "Wabbajack is running inside your Downloads folder. This folder is often highly monitored by antivirus software and these can often " +
                    "conflict with the operations Wabbajack needs to perform. Please move this executable outside of your Downloads folder and then restart the app.",
                    "Cannot run inside Downloads",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Environment.Exit(1);
            }

            Mode = mode;

            // Define commands
            this.ChangePathCommand = ReactiveCommand.Create(ExecuteChangePath);
            this.ChangeDownloadPathCommand = ReactiveCommand.Create(ExecuteChangeDownloadPath);
            this.ShowReportCommand = ReactiveCommand.Create(ShowReport);
            this.VisitNexusSiteCommand = ReactiveCommand.Create(VisitNexusSite);
            this.OpenModListPropertiesCommand = ReactiveCommand.Create(OpenModListProperties);
            this.OpenReadmeCommand = ReactiveCommand.Create(
                execute: this.OpenReadmeWindow,
                canExecute: Observable.CombineLatest(
                        this.WhenAny(x => x.ModList)
                            .Select(modList => !string.IsNullOrEmpty(modList?.Readme)),
                        this.WhenAny(x => x.UIReady),
                        resultSelector: (modListExists, uiReady) => modListExists && uiReady)
                    .ObserveOnGuiThread());
            this.BeginCommand = ReactiveCommand.Create(
                execute: this.ExecuteBegin,
                canExecute: this.WhenAny(x => x.UIReady)
                    .ObserveOnGuiThread());

            // Apply modlist properties when it changes
            this.WhenAny(x => x.ModList)
                .NotNull()
                .Subscribe(modList =>
                {
                    this.SplashScreenModName = modList.Name;
                    this.SplashScreenAuthorName = modList.Author;
                    this._nexusSiteURL = modList.Website;
                    this.SplashScreenSummary = modList.Description;
                })
                .DisposeWith(this.CompositeDisposable);

            _slideShow = new SlideShow(this, true);

            // Update splashscreen when modlist changes
            Observable.CombineLatest(
                    this.WhenAny(x => x.ModList),
                    this.WhenAny(x => x.ModListPath),
                    this.WhenAny(x => x.EnableSlideShow),
                    (modList, modListPath, enableSlideShow) => (modList, modListPath, enableSlideShow))
                // Do any potential unzipping on a background thread
                .ObserveOn(RxApp.TaskpoolScheduler)
                .Select(u =>
                {
                    if (u.enableSlideShow
                        && u.modList != null
                        && u.modListPath != null
                        && File.Exists(u.modListPath)
                        && !string.IsNullOrEmpty(u.modList.Image)
                        && u.modList.Image.Length == 36)
                    {
                        try
                        {
                            using (var fs = new FileStream(u.modListPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var ar = new ZipArchive(fs, ZipArchiveMode.Read))
                            using (var ms = new MemoryStream())
                            {
                                var entry = ar.GetEntry(u.modList.Image);
                                using (var e = entry.Open())
                                    e.CopyTo(ms);
                                var image = new BitmapImage();
                                image.BeginInit();
                                image.CacheOption = BitmapCacheOption.OnLoad;
                                image.StreamSource = ms;
                                image.EndInit();
                                image.Freeze();

                                return image;
                            }
                        }
                        catch (Exception)
                        {
                            this.LogMsg("Error loading splash image.");
                        }
                    }
                    return _wabbajackLogo;
                })
                .ObserveOn(RxApp.MainThreadScheduler)
                .StartWith(_wabbajackLogo)
                .Subscribe(bitmap => this.SplashScreenImage = bitmap)
                .DisposeWith(this.CompositeDisposable);

            /// Wire slideshow updates
            // Merge all the sources that trigger a slideshow update
            Observable.Merge(
                    // If the natural timer fires
                    Observable.Interval(TimeSpan.FromSeconds(10)).Unit(),
                    // If user requests one manually
                    this.SlideShowNextItemCommand.StartingExecution())
                // When enabled, fire an initial signal
                .StartWith(Unit.Default)
                // Only subscribe to slideshow triggers if enabled and installing
                .FilterSwitch(
                    Observable.CombineLatest(
                        this.WhenAny(x => x.EnableSlideShow),
                        this.WhenAny(x => x.Installing),
                        resultSelector: (enabled, installing) => enabled && installing))
                // Don't ever update more than once every half second.  ToDo: Update to debounce
                .Throttle(TimeSpan.FromMilliseconds(500), RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => _slideShow.UpdateSlideShowItem())
                .DisposeWith(this.CompositeDisposable);

            // Initialize work queue
            WorkQueue.Init(
                report_function: (id, msg, progress) => this._statusSubject.OnNext(new CPUStatus() { ID = id, Msg = msg, Progress = progress }),
                report_queue_size: (max, current) => this.SetQueueSize(max, current));
            // Compile progress updates and populate ObservableCollection
            this._statusSubject
                .ObserveOn(RxApp.TaskpoolScheduler)
                .ToObservableChangeSet(x => x.ID)
                .Batch(TimeSpan.FromMilliseconds(250))
                .EnsureUniqueChanges()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Sort(SortExpressionComparer<CPUStatus>.Ascending(s => s.ID), SortOptimisations.ComparesImmutableValuesOnly)
                .Bind(this.Status)
                .Subscribe()
                .DisposeWith(this.CompositeDisposable);
        }

        public ObservableCollection<string> Log { get; } = new ObservableCollection<string>();

        private string _Location;
        public string Location { get => _Location; set => this.RaiseAndSetIfChanged(ref _Location, value); }

        private string _LocationLabel;
        public string LocationLabel { get => _LocationLabel; set => this.RaiseAndSetIfChanged(ref _LocationLabel, value); }

        private string _DownloadLocation;
        public string DownloadLocation { get => _DownloadLocation; set => this.RaiseAndSetIfChanged(ref _DownloadLocation, value); }

        private int _queueProgress;
        public int QueueProgress { get => _queueProgress; set => this.RaiseAndSetIfChanged(ref _queueProgress, value); }

        public string LogFile { get; }

        private void ExecuteChangePath()
        {
            if (Mode == TaskMode.INSTALLING)
            {
                var folder = UIUtils.ShowFolderSelectionDialog("Select Installation directory");
                if (folder == null) return;
                Location = folder;
                if (DownloadLocation == null)
                    DownloadLocation = Path.Combine(Location, "downloads");
            }
            else
            {
                var folder = UIUtils.ShowFolderSelectionDialog("Select Your MO2 profile directory");
                Location = folder;
            }
        }

        private void ExecuteChangeDownloadPath()
        {
            var folder = UIUtils.ShowFolderSelectionDialog("Select a location for MO2 downloads");
            if (folder != null) DownloadLocation = folder;
        }

        private void ShowReport()
        {
            var file = Path.GetTempFileName() + ".html";
            File.WriteAllText(file, HTMLReport);
            Process.Start(file);
        }

        public string _nexusSiteURL = null;
        private void VisitNexusSite()
        {
            if (_nexusSiteURL != null && _nexusSiteURL.StartsWith("https://"))
            {
                Process.Start(_nexusSiteURL);
            }
        }

        private ModlistPropertiesWindow modlistPropertiesWindow;
        public string newImagePath;
        public string readmePath;
        public bool ChangedProperties;
        private void OpenModListProperties()
        {
            if (UIReady)
            {
                if (modlistPropertiesWindow == null)
                {
                    modlistPropertiesWindow = new ModlistPropertiesWindow(this);
                    newImagePath = null;
                    ChangedProperties = false;

                }
                if(!modlistPropertiesWindow.IsClosed)
                    modlistPropertiesWindow.Show();
                else
                {
                    modlistPropertiesWindow = null;
                    OpenModListProperties();
                }
            }
        }

        private void OpenReadmeWindow()
        {
            if (!UIReady || string.IsNullOrEmpty(this.ModList.Readme)) return;
            var text = "";
            using (var fs = new FileStream(this.ModListPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var ar = new ZipArchive(fs, ZipArchiveMode.Read))
            using (var ms = new MemoryStream())
            {
                var entry = ar.GetEntry(this.ModList.Readme);
                using (var e = entry.Open())
                    e.CopyTo(ms);
                ms.Seek(0, SeekOrigin.Begin);
                using (var sr = new StreamReader(ms))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                        text += line+Environment.NewLine;
                }
            }

            var viewer = new TextViewer(text, this.ModListName);
            viewer.Show();
        }

        private string _SplashScreenModName = "Wabbajack";
        public string SplashScreenModName { get => _SplashScreenModName; set => this.RaiseAndSetIfChanged(ref _SplashScreenModName, value); }

        private string _SplashScreenAuthorName = "Halgari & the Wabbajack Team";
        public string SplashScreenAuthorName { get => _SplashScreenAuthorName; set => this.RaiseAndSetIfChanged(ref _SplashScreenAuthorName, value); }

        private string _SplashScreenSummary;
        public string SplashScreenSummary { get => _SplashScreenSummary; set => this.RaiseAndSetIfChanged(ref _SplashScreenSummary, value); }
        private bool _splashShowNSFW = false;
        public bool SplashShowNSFW { get => _splashShowNSFW; set => this.RaiseAndSetIfChanged(ref _splashShowNSFW, value); }

        public string Error => "Error";

        public string this[string columnName] => Validate(columnName);

        private string Validate(string columnName)
        {
            string validationMessage = null;
            switch (columnName)
            {
                case "Location":
                    if (Location == null)
                    {
                        validationMessage = null;
                    }
                    else switch (Mode)
                    {
                        case TaskMode.BUILDING when Location != null && Directory.Exists(Location) && File.Exists(Path.Combine(Location, "modlist.txt")):
                            Location = Path.Combine(Location, "modlist.txt");
                            validationMessage = null;
                            ConfigureForBuild();
                            break;
                        case TaskMode.INSTALLING when Location != null && Directory.Exists(Location) && !Directory.EnumerateFileSystemEntries(Location).Any():
                            validationMessage = null;
                            break;
                        case TaskMode.INSTALLING when Location != null && Directory.Exists(Location) && Directory.EnumerateFileSystemEntries(Location).Any():
                            validationMessage = "You have selected a non-empty directory. Installing the modlist here might result in a broken install!";
                            break;
                        default:
                            validationMessage = "Invalid Mod Organizer profile directory";
                            break;
                    }
                    break;
            }
            return validationMessage;
        }

        public void LogMsg(string msg)
        {
            Application.Current.Dispatcher.Invoke(() => Log.Add(msg));
        }

        public void SetQueueSize(int max, int current)
        {
            if (max == 0)
                max = 1;
            var total = current * 100 / max;
            QueueProgress = total;
        }

        private void ConfigureForBuild()
        {
            var profile_folder = Path.GetDirectoryName(Location);
            var mo2folder = Path.GetDirectoryName(Path.GetDirectoryName(profile_folder));
            if (!File.Exists(Path.Combine(mo2folder, "ModOrganizer.exe")))
                LogMsg($"Error! No ModOrganizer2.exe found in {mo2folder}");

            var profile_name = Path.GetFileName(profile_folder);
            this.ModListName = profile_name;
            Mode = TaskMode.BUILDING;

            var tmp_compiler = new Compiler(mo2folder);
            DownloadLocation = tmp_compiler.MO2DownloadsFolder;

            _mo2Folder = mo2folder;
        }

        internal void ConfigureForInstall(string source, ModList modlist)
        {
            this.ModList = modlist;
            this.ModListPath = source;
            Mode = TaskMode.INSTALLING;
            ModListName = this.ModList.Name;
            HTMLReport = this.ModList.ReportHTML;
            Location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            _slideShow.SlideShowElements = modlist.Archives
                .Select(m => m.State)
                .OfType<NexusDownloader.State>()
                .Select(m => 
                new Slide(NexusApiUtils.FixupSummary(m.ModName),m.ModID,
                    NexusApiUtils.FixupSummary(m.Summary), NexusApiUtils.FixupSummary(m.Author),
                    m.Adult,m.NexusURL,m.SlideShowPic)).ToList();


            _slideShow.PreloadSlideShow();
        }

        private void ExecuteBegin()
        {
            UIReady = false;
            if (Mode == TaskMode.INSTALLING)
            {
                this.Installing = true;
                var installer = new Installer(this.ModListPath, this.ModList, Location)
                {
                    DownloadFolder = DownloadLocation
                };
                var th = new Thread(() =>
                {
                    UIReady = false;
                    try
                    {
                        installer.Install();
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        LogMsg(ex.StackTrace);
                        LogMsg(ex.ToString());
                        LogMsg($"{ex.Message} - Can't continue");
                    }
                    finally
                    {
                        UIReady = true;
                        this.Installing = false;
                    }
                })
                {
                    Priority = ThreadPriority.BelowNormal
                };
                th.Start();
            }
            else if (_mo2Folder != null)
            {
                var compiler = new Compiler(_mo2Folder)
                {
                    MO2Profile = ModListName,
                    ModListName = ChangedProperties ? SplashScreenModName : null,
                    ModListAuthor = ChangedProperties ? SplashScreenAuthorName : null,
                    ModListDescription = ChangedProperties ? SplashScreenSummary : null,
                    ModListImage = ChangedProperties ? newImagePath : null,
                    ModListWebsite = ChangedProperties ? _nexusSiteURL : null,
                    ModListReadme = ChangedProperties ? readmePath : null
                };
                var th = new Thread(() =>
                {
                    UIReady = false;
                    try
                    {
                        compiler.Compile();
                        if (compiler.ModList != null && compiler.ModList.ReportHTML != null)
                            HTMLReport = compiler.ModList.ReportHTML;
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        LogMsg(ex.StackTrace);
                        LogMsg(ex.ToString());
                        LogMsg($"{ex.Message} - Can't continue");
                    }
                    finally
                    {
                        UIReady = true;
                    }
                })
                {
                    Priority = ThreadPriority.BelowNormal
                };
                th.Start();
            }
            else
            {
                Utils.Log("Cannot compile modlist: no valid Mod Organizer profile directory selected.");
                UIReady = true;
            }
        }
    }

    public class CPUStatus
    {
        public int Progress { get; internal set; }
        public string Msg { get; internal set; }
        public int ID { get; internal set; }
    }
}