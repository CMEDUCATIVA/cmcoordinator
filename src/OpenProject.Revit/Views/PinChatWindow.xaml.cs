using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenProject.Revit.ViewModels;

namespace OpenProject.Revit.Views
{
  public partial class PinChatWindow : Window
  {
    public PinChatWindow(PinChatViewModel viewModel)
    {
      InitializeComponent();
      DataContext = viewModel;
      var name = string.IsNullOrWhiteSpace(viewModel.PinName) ? viewModel.WorkPackageId : viewModel.PinName;
      Title = $"{viewModel.WorkPackageId} - Anotaciones - {name}";
    }

    private void OnMessageInputKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key != Key.Enter)
        return;

      if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        return;

      var vm = DataContext as PinChatViewModel;
      if (vm?.SendCommand?.CanExecute(null) == true)
      {
        vm.SendCommand.Execute(null);
        e.Handled = true;
      }
    }

    private void OnMessageInputTextChanged(object sender, TextChangedEventArgs e)
    {
      if (MessageInput == null)
        return;

      var lineCount = Math.Max(1, MessageInput.LineCount);
      var lineHeight = MessageInput.FontSize * 1.6;
      var padding = MessageInput.Padding.Top + MessageInput.Padding.Bottom;
      var desired = (lineCount * lineHeight) + padding + 6;
      if (desired < MessageInput.MinHeight)
        desired = MessageInput.MinHeight;
      if (desired > MessageInput.MaxHeight)
        desired = MessageInput.MaxHeight;
      MessageInput.Height = desired;
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
      if (sender is not Button button || button.ContextMenu == null)
        return;

      button.ContextMenu.PlacementTarget = button;
      button.ContextMenu.IsOpen = true;
    }

    private void OnEmojiItemClick(object sender, RoutedEventArgs e)
    {
      if (sender is not MenuItem item)
        return;

      var emoji = item.Tag?.ToString() ?? item.Header?.ToString();
      if (string.IsNullOrWhiteSpace(emoji) || MessageInput == null)
        return;

      MessageInput.Text += emoji;
      MessageInput.CaretIndex = MessageInput.Text.Length;
      MessageInput.Focus();
    }
  }
}
