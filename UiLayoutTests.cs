namespace AllianceWatch;

internal static class UiLayoutTests
{
    internal static void Verify(params Form[] forms)
    {
        foreach (var form in forms)
        {
            foreach (var size in new[] { form.MinimumSize, form.Size, form is MainForm ? new Size(1600, 900) : form.Size }.Distinct())
            {
                form.Size = size;
                form.CreateControl();
                LayoutTree(form);
                VerifyBars(form, size);
                foreach (var tabs in form.Controls.OfType<TabControl>())
                {
                    foreach (TabPage page in tabs.TabPages)
                    {
                        foreach (var bar in Descendants(page).OfType<WrappingToolbar>())
                        {
                            // Hidden WinForms tabs retain a 200px design-time
                            // width. Exercise each bar at its eventual page width.
                            bar.Width = Math.Max(400, form.ClientSize.Width - 60);
                            bar.PerformLayout();
                        }
                        VerifyBars(page, size);
                    }
                }
                if (form is MapForm or DatabaseBrowserForm)
                {
                    var split = form.Controls.OfType<SplitContainer>().Single();
                    Require(split.Panel1.Width >= 480 && split.Panel2.Width >= 290,
                        $"{form.Text}: map/table or details pane collapsed at {size.Width}px");
                }
                if (form is MainForm main) VerifyOperator(main);
            }
        }
    }

    private static void VerifyOperator(MainForm main)
    {
        var operatorPanel = Descendants(main).OfType<TelemetryPanel>()
            .Single(panel => panel.Caption == "OPERATOR CONTROL");
        var actions = Descendants(operatorPanel).OfType<Button>().ToArray();
        Require(actions.Length == 8, "Operator control must expose all eight actions");
        Require(actions.All(button => button.Height >= 29), "Operator actions need full-height click targets");
        var ordered = actions.OrderBy(button => button.Top).ToArray();
        Require(ordered.Zip(ordered.Skip(1)).All(pair => pair.First.Bottom <= pair.Second.Top),
            "Operator actions must not overlap");
        var viewport = operatorPanel.Parent?.Parent as Panel;
        Require(viewport?.AutoScroll == true, "The right rail must scroll on short screens");
        var title = Descendants(main).OfType<Label>().Single(label => label.Text == "ALLIANCEWATCH");
        Require(title.Width >= 180, $"Dashboard title must remain readable at compact widths ({title.Width}px in {main.ClientSize.Width}px window)");
    }

    private static void VerifyBars(Control root, Size size)
    {
        foreach (var bar in Descendants(root).OfType<WrappingToolbar>())
        {
            // WinForms leaves unselected tab pages at their design-time
            // 200px width until the tab is activated.
            if (bar.ClientSize.Width < Math.Min(400, root.FindForm()?.ClientSize.Width / 2 ?? 400)) continue;
            foreach (Control child in bar.Controls)
            {
                Require(child.Right <= bar.ClientSize.Width + 1,
                    $"{root.FindForm()?.Text}: toolbar action '{child.Text}' ({child.GetType().Name}, {child.Bounds}) extends past {bar.ClientSize.Width}px at {size.Width}px in {bar.Parent?.Text}");
                Require(child.Bottom <= bar.Height + 1,
                    $"{root.FindForm()?.Text}: toolbar action '{child.Text}' ({child.Bounds}) is vertically clipped at {size.Width}px; bar={bar.Bounds}, padding={bar.Padding}");
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void LayoutTree(Control root)
    {
        root.PerformLayout();
        foreach (Control child in root.Controls) LayoutTree(child);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
