using Varco.Core.Control;

namespace Varco.App;

/// <summary>Richiesta in arrivo da un altro PC: si mostra in primo piano con il codice da confrontare.</summary>
internal sealed class PairRequestForm : Form
{
    public Guid RequestId { get; }

    public PairRequestForm(PairRequestDto req)
    {
        RequestId = req.RequestId;
        Text = "Varco — richiesta di collegamento";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 16, 20, 16);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(root);

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        header.Controls.Add(new PictureBox { Image = VarcoIcon.RenderApp(40), Size = new Size(40, 40), Margin = new Padding(0, 0, 12, 0) });
        header.Controls.Add(new Label
        {
            Text = $"{req.PeerName} vuole usare mouse e tastiera insieme a questo PC.",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 0),
        });
        root.Controls.Add(header);

        root.Controls.Add(new Label
        {
            Text = $"Accetta solo se su {req.PeerName} vedi lo stesso codice:",
            AutoSize = true,
            MaximumSize = new Size(372, 0),
            Margin = new Padding(0, 12, 0, 4),
        });
        root.Controls.Add(new Label
        {
            Text = req.Code,
            AutoSize = true,
            Font = new Font(FontFamily.GenericMonospace, 26f, FontStyle.Bold),
            ForeColor = VarcoIcon.Brand,
            Margin = new Padding(0, 0, 0, 12),
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var accept = new Button { Text = "Accetta", Width = 110, Height = 32 };
        var reject = new Button { Text = "Rifiuta", Width = 110, Height = 32 };
        accept.Click += async (_, _) => await Answer(true);
        reject.Click += async (_, _) => await Answer(false);
        buttons.Controls.Add(accept);
        buttons.Controls.Add(reject);
        root.Controls.Add(buttons);
        AcceptButton = accept;
        CancelButton = reject;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    private async Task Answer(bool accept)
    {
        Enabled = false;
        try { await ControlClient.SendAsync(new ControlRequest { Cmd = "pairAnswer", RequestId = RequestId, Accept = accept }, 5000); }
        catch { }
        Close();
    }
}
