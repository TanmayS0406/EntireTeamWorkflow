using Microsoft.AspNetCore.Components;

namespace Etw.Desktop.UI.Components;

/// <summary>Base class for components that show <see cref="AppState"/>: re-renders whenever it changes, from any thread.</summary>
public abstract class StateComponent : ComponentBase, IDisposable
{
    [Inject] protected AppState State { get; set; } = default!;

    protected override void OnInitialized() => State.Changed += OnStateChanged;

    private void OnStateChanged() => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose() => State.Changed -= OnStateChanged;

    protected static string F(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
