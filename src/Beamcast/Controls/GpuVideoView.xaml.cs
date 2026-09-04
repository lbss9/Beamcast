using Beamcast.Codec.Gpu;
using Beamcast.Render;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Controls;

/// <summary>A SwapChainPanel wrapped so pages can hand it a <see cref="SwapChainPresenter"/>.</summary>
public sealed partial class GpuVideoView : UserControl
{
    private SwapChainPresenter? _presenter;

    public GpuVideoView()
    {
        InitializeComponent();
        Loaded += (_, _) => TryAttach();
        // Pages often bind while the panel is still collapsed (zero size); attach once it has a size.
        Panel.SizeChanged += (_, _) => TryAttach();
    }

    public SwapChainPresenter? Presenter => _presenter;

    public bool HasFrame { get; private set; }

    /// <summary>Binds the view to a device-owned presenter. Safe to call before the control is loaded.</summary>
    public void Bind(SwapChainPresenter presenter)
    {
        if (ReferenceEquals(_presenter, presenter) && presenter.IsAttached)
            return;
        if (!ReferenceEquals(_presenter, presenter))
            _presenter?.Detach();
        _presenter = presenter;
        HasFrame = false;
        TryAttach();
    }

    public void Unbind()
    {
        _presenter?.Detach();
        _presenter = null;
        HasFrame = false;
    }

    public void MarkFrame() => HasFrame = true;

    public void Clear()
    {
        HasFrame = false;
        _presenter?.Clear();
    }

    private void TryAttach()
    {
        if (_presenter is null || _presenter.IsAttached || !IsLoaded || Panel.ActualWidth <= 0 || Panel.ActualHeight <= 0)
            return;
        try
        {
            _presenter.Attach(Panel);
        }
        catch (Exception)
        {
            // Retried on the next size change.
        }
    }
}
