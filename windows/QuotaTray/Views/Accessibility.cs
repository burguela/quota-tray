using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace QuotaTray.Views;

/// <summary>
/// A border that acts as a button (capsules, small buttons, segments, menu entries, the caret). It
/// tells screen readers it's a button with a name, and lets them press it.
/// </summary>
public sealed class Pressable : Border
{
    /// <summary>What a press does; set by <c>PopupWindow.Clickable</c>.</summary>
    internal Action? OnPress { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new PressablePeer(this);

    private sealed class PressablePeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        public PressablePeer(Pressable owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(Pressable);
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }
            ((Pressable)Owner).OnPress?.Invoke();
        }
    }
}

/// <summary>Screen-reader support for <see cref="ToggleSwitch"/>: a named toggle it can flip.</summary>
internal sealed class ToggleSwitchPeer : FrameworkElementAutomationPeer, IToggleProvider
{
    public ToggleSwitchPeer(ToggleSwitch owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
    protected override string GetClassNameCore() => nameof(ToggleSwitch);
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Toggle ? this : base.GetPattern(patternInterface);

    public ToggleState ToggleState => ((ToggleSwitch)Owner).IsOn ? ToggleState.On : ToggleState.Off;

    public void Toggle()
    {
        if (!IsEnabled())
        {
            throw new ElementNotEnabledException();
        }
        ((ToggleSwitch)Owner).Flip();
    }

    /// <summary>Tells a listening screen reader the switch changed.</summary>
    internal void RaiseToggled(bool wasOn, bool isOn) =>
        RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
            wasOn ? ToggleState.On : ToggleState.Off, isOn ? ToggleState.On : ToggleState.Off);
}
