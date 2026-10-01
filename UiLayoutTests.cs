// SPDX-License-Identifier: LicenseRef-AllianceWatch-Free-Use-No-Resale
// Copyright (c) 2026 Jesse Lee Shelley. All Rights Reserved.
// Free to run; selling or paid access requires Owner's paid written permission.
// See LICENSE and NOTICE for terms and required attribution.
// Creator: https://linkedin.com/in/jesse-shelley
// Repository: https://github.com/ultros/alliance-watch

namespace AllianceWatch;

internal static class UiLayoutTests
{
    internal static void Verify(params Form[] forms)
    {
        foreach (var form in forms)
        {
            var sizes = new[] { form.MinimumSize, form.Size }.Concat(form is MainForm
                ? new[] { new Size(1366, 768), new Size(1600, 900), new Size(3440, 1440) } : []).Distinct();
            foreach (var size in sizes)
            {
                form.Size = size;
                form.CreateControl();
                LayoutTree(form);
                VerifyBars(form, size);
                foreach (var tabs in form.Controls.OfType<TabControl>())
                {
                    var selected = tabs.SelectedIndex;
                    foreach (TabPage page in tabs.TabPages)
                    {
                        tabs.SelectedTab = page;
                        LayoutTree(form);
                        VerifyBars(page, size);
                    }
                    tabs.SelectedIndex = selected;
                }
                if (form is MapForm or DatabaseBrowserForm)
                {
                    var split = form.Controls.OfType<SplitContainer>().Single();
                    Require(split.Panel1.Width >= 480 && split.Panel2.Width >= 290,
                        $"{form.Text}: map/table or details pane collapsed at {size.Width}px");
                }
                if (form is MainForm main) VerifyOperator(main);
                if (form is DatabaseCompressionForm)
                {
                    foreach (var child in Descendants(form))
                        Require(child.Parent!.ClientRectangle.Contains(child.Bounds), $"Compression control is outside the dialog: {child.GetType().Name} {child.Text}");
                    foreach (var label in Descendants(form).OfType<Label>())
                    {
                        var preferred = label.GetPreferredSize(new Size(label.Width, 0));
                        Require(label.Height >= preferred.Height, $"Compression text is clipped: {label.Text}");
                    }
                    var details = Descendants(form).OfType<TextBox>().Single();
                    Require(details.Height >= 60, "Compression results need a readable scrolling area");
                }
            }
        }
    }

    private static void VerifyOperator(MainForm main)
    {
        var operatorPanel = Descendants(main).OfType<TelemetryPanel>()
            .Single(panel => panel.Caption == "OPERATOR CONTROL");
        var actions = Descendants(operatorPanel).OfType<Button>().ToArray();
        Require(actions.Length == 9, "Operator control must expose all nine actions, including database compression");
        Require(actions.All(button => button.Height >= 29), "Operator actions need full-height click targets");
        var ordered = actions.OrderBy(button => button.Top).ToArray();
        Require(ordered.Zip(ordered.Skip(1)).All(pair => pair.First.Bottom <= pair.Second.Top),
            "Operator actions must not overlap");
        Require(operatorPanel.Parent!.ClientRectangle.Contains(operatorPanel.Bounds),
            "All operator actions must remain visible without scrolling");
        foreach (var button in actions)
        {
            Require(button.Parent!.ClientRectangle.Contains(button.Bounds), $"Operator action is clipped: {button.Text}");
            VerifyButtonText(button);
        }
        var viewport = Descendants(main).OfType<Panel>().Single(panel => panel.Name == "right-telemetry-viewport");
        Require(viewport.AutoScroll && viewport.Height >= 60, "Telemetry must remain scrollable beside the visible operator actions");
        var searchBar = Descendants(main).OfType<WrappingToolbar>().Single(bar => bar.Name == "dashboard-search-toolbar");
        Require(searchBar.Controls.OfType<Button>().Count() == 3 && searchBar.Controls.OfType<TextBox>().Count() == 1,
            "Signal filters and all-article search must remain available at every width");
        var searchControls = searchBar.Controls.Cast<Control>().Where(control => control is Button or TextBox).ToArray();
        for (var first = 0; first < searchControls.Length; first++)
            for (var second = first + 1; second < searchControls.Length; second++)
                Require(!searchControls[first].Bounds.IntersectsWith(searchControls[second].Bounds),
                    "Dashboard search and filter controls must not overlap after resizing");
        var title = Descendants(main).OfType<Label>().Single(label => label.Text == "ALLIANCEWATCH");
        Require(title.Width >= 180, $"Dashboard title must remain readable at compact widths ({title.Width}px in {main.ClientSize.Width}px window)");
        foreach (var name in new[] { "developer-attribution", "developer-links" })
        {
            var credit = Descendants(main).OfType<Label>().Single(label => label.Name == name);
            var creditSize = TextRenderer.MeasureText(credit.Text, credit.Font);
            Require(credit.Width >= creditSize.Width && credit.Height >= creditSize.Height,
                $"Developer attribution and links must remain fully readable at {main.ClientSize.Width}px");
        }
    }

    private static void VerifyBars(Control root, Size size)
    {
        foreach (var bar in Descendants(root).OfType<WrappingToolbar>())
        {
            // Only test the selected page; the loop above selects every page in turn.
            var page = bar.Parent;
            while (page is not null && page is not TabPage) page = page.Parent;
            if (page is TabPage tabPage && tabPage.Parent is TabControl tabs && tabs.SelectedTab != tabPage) continue;
            foreach (Control child in bar.Controls)
            {
                Require(child.Right <= bar.ClientSize.Width + 1,
                    $"{root.FindForm()?.Text}: toolbar action '{child.Text}' ({child.GetType().Name}, {child.Bounds}) extends past {bar.ClientSize.Width}px at {size.Width}px in {bar.Parent?.Text}");
                Require(child.Bottom <= bar.Height + 1,
                    $"{root.FindForm()?.Text}: toolbar action '{child.Text}' ({child.Bounds}) is vertically clipped at {size.Width}px; bar={bar.Bounds}, padding={bar.Padding}");
                if (child is Button button) VerifyButtonText(button);
            }
        }
    }

    internal static void VerifyButtonText(Button button)
    {
        var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        Require(button.ClientSize.Width >= text.Width + button.Padding.Horizontal + 12 && button.ClientSize.Height >= text.Height + 4,
            $"Button label is clipped: '{button.Text}' ({button.ClientSize}; text {text})");
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void LayoutTree(Control root)
    {
        root.PerformLayout();
        // Unshown fixture forms leave tab pages at their 200px design size.
        // Use the tab's actual display rectangle, then lay out the whole page.
        if (root is TabControl tabs && tabs.SelectedTab is { } page) page.Bounds = tabs.DisplayRectangle;
        foreach (Control child in root.Controls) LayoutTree(child);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
