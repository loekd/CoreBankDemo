using CoreBankDemo.DemoRunner.Application;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

internal sealed record ConfirmationRequest(string Title, string Command, IReadOnlyList<string> Instances);

internal interface IConfirmationService
{
    bool Confirm(ConfirmationRequest request);
}

#pragma warning disable CS0618
internal sealed class TerminalConfirmationService : IConfirmationService
{
    public bool Confirm(ConfirmationRequest request)
    {
        var dialog = new DestructiveConfirmationDialog(request);
        dialog.FocusCancel();
        AppTerminal.Run(dialog);
        return dialog.Result == true;
    }
}

internal sealed class DestructiveConfirmationDialog : Dialog<bool>
{
    internal DestructiveConfirmationDialog(ConfirmationRequest request)
    {
        Title = request.Title;
        Width = 72;
        Height = 10;
        OperatorTheme.Apply(this, OperatorTheme.OverlayScheme);

        CancelButton = new Button { Text = "Cancel", IsDefault = true };
        CancelButton.Accepting += (_, e) =>
        {
            e.Handled = true;
            Cancel();
        };

        var instances = request.Instances.Count == 0
            ? "(no verified instances)"
            : string.Join(", ", request.Instances);
        Add(new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 4,
            Text = $"{request.Command}{Environment.NewLine}Affected instances: {instances}{Environment.NewLine}Press uppercase Y to confirm. Enter/Escape cancel.",
        });
        AddButton(CancelButton);
        DefaultAcceptView = CancelButton;
    }

    internal Button CancelButton { get; }
    internal int ConfirmationCount { get; private set; }

    protected override bool OnKeyDown(Key key)
    {
        if (InteractionPolicies.ConfirmsDestructiveAction((char)key.AsRune.Value))
        {
            key.Handled = true;
            if (Result != true)
            {
                ConfirmationCount++;
                Result = true;
                RequestStop();
            }

            return true;
        }

        if (key == Key.Esc || key == Key.Enter)
        {
            key.Handled = true;
            Cancel();
            return true;
        }

        return base.OnKeyDown(key);
    }

    internal void FocusCancel() => CancelButton.SetFocus();
    internal bool HandleKeyForTest(Key key) => OnKeyDown(key);

    private void Cancel()
    {
        Result = false;
        RequestStop();
    }
}
#pragma warning restore CS0618
