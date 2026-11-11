using Varco.Core.Control;

namespace Varco.App;

/// <summary>Richiesta in arrivo da un altro PC: si mostra in primo piano con il codice da confrontare.</summary>
internal sealed class PairRequestForm : Form
{
    public Guid RequestId { get; }

    public PairRequestForm(PairRequestDto req)
    {
        RequestId = req.RequestId;
        Ui.StyleForm(this);
        Text = "Varco — richiesta di collegamento";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Ui.P(22, 20, 22, 18);

        var w = Ui.S(380);
        var root = Ui.Column();
        root.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(root);

        var header = Ui.Flow();
        header.Controls.Add(new PictureBox { Image = VarcoIcon.RenderApp(Ui.S(44), gradient: true), Size = Ui.S(44, 44), Margin = Ui.P(0, 0, 14, 0) });
        header.Controls.Add(Ui.Label($"{req.PeerName} vuole usare mouse e tastiera insieme a questo PC.", Ui.Section, maxWidth: w - Ui.S(58), margin: Ui.P(0, 2, 0, 0)));
        root.Controls.Add(header);

        var box = new Tint(Ui.AccentSoft) { Margin = Ui.P(0, 16, 0, 0), MinimumSize = new Size(w, 0), MaximumSize = new Size(w, 0) };
        var inner = Ui.Column();
        inner.Location = new Point(box.Padding.Left, box.Padding.Top);
        inner.Controls.Add(Ui.Label($"Accetta solo se su {req.PeerName} vedi lo stesso codice:", color: Ui.AccentText, maxWidth: w - box.Padding.Horizontal));
        inner.Controls.Add(Ui.Label(req.Code, Ui.Code, Ui.Accent, margin: Ui.P(0, 4, 0, 0)));
        box.Controls.Add(inner);
        root.Controls.Add(box);

        var accept = new VButton("Accetta", primary: true, minWidth: 110);
        var reject = new VButton("Rifiuta", minWidth: 110);
        accept.Click += async (_, _) => await Answer(true);
        reject.Click += async (_, _) => await Answer(false);
        var buttons = Ui.Flow();
        reject.Margin = Ui.P(8, 0, 0, 0);
        buttons.Controls.Add(accept);
        buttons.Controls.Add(reject);
        var footer = new LineRow(w, accept.Height, [], buttons) { Margin = Ui.P(0, 18, 0, 0) };
        buttons.Size = buttons.PreferredSize;
        root.Controls.Add(footer);
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
