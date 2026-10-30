// Test classes in this assembly build WPF views (PlayerView, ShellWindow, HomeView,
// SettingsWindow) on their own STA threads. WPF's resource package is not safe for
// concurrent InitializeComponent/LoadComponent across threads: CI run 41052665982 had
// 37 tests fail with a NullReferenceException in System.IO.Packaging.PackagePart
// .IsStreamClosed once a fourth view-building class (Cp2PlayerTests) joined the
// parallel set. The suite runs serially (about 95 s of test time).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
