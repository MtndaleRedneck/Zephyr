using System;
using System.Windows;
namespace ZephyrPlayer {
 public partial class PairingWindow : Window {
  private readonly PairingPanel _panel;
  public PairingWindow(PairingGate gate, PairedStore store, Action<string> started) {
   InitializeComponent(); WindowChrome.ApplyDarkTitleBar(this); MaxHeight = SystemParameters.WorkArea.Height;
   _panel = new PairingPanel(gate, store, started); Host.Content = _panel;
  }
  public new object FindName(string name) => _panel.FindName(name) ?? base.FindName(name);
  public void RefreshPhones() => _panel.RefreshPhones();
  public void OnPairSucceeded() => _panel.OnPairSucceeded();
 }
}