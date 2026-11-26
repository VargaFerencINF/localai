using EfMailAgent.Email;

namespace EfMailAgent.Tests.Email;

public class EmailFileTests
{
    [Fact]
    public void Demo_eml_is_parsed()
    {
        var email = EmailFile.Load(Path.Combine(TestPaths.SamplesRoot, "emails", "demo_eloreendeles_01.eml"));

        Assert.Equal("<fwd-demo-01@ertekesites.example>", email.MessageId);
        Assert.Equal("Fwd: Előrendelés: Horgász Centrum Kft., tavaszi szezon", email.Subject);
        Assert.Equal("Kovács Péter <kovacs.peter@ertekesites.example>", email.From);
        Assert.Equal("eloreendeles@ertekesites.example", email.To);
        Assert.Equal(new DateTimeOffset(2026, 11, 25, 9, 41, 0, TimeSpan.FromHours(1)), email.ReceivedAt);
        Assert.StartsWith("Sziasztok,", email.BodyText);
        Assert.Contains("- Feeder bot 3,6 m, 120 g: 10 db, 18 500 Ft/db", email.BodyText);
        Assert.Empty(email.AttachmentNames);
    }

    [Fact]
    public void Txt_file_is_the_body()
    {
        var path = Path.Combine(TestPaths.SamplesRoot, "emails", "elorendeles_02_egy_mondat.txt");
        var email = EmailFile.Load(path);
        Assert.Null(email.MessageId);
        Assert.Equal(File.ReadAllText(path), email.BodyText);
    }

    [Fact]
    public void Html_only_eml_and_attachments()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile("html.eml", """
            Message-ID: <h@x>
            Subject: =?UTF-8?B?RWzFkXJlbmRlbMOpcw==?=
            From: "Vevő, Kft." <v@x.example>
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/html; charset=UTF-8

            <html><head><style>p{color:red}</style></head><body><p>Kérünk:</p><ul><li>10 db bot</li><li>5&nbsp;doboz horog</li></ul></body></html>
            --b
            Content-Type: application/pdf
            Content-Disposition: attachment; filename="rendeles.pdf"

            JVBERi0=
            --b--
            """.Replace("\n", "\r\n"));

        var email = EmailFile.Load(path);

        Assert.Equal("Előrendelés", email.Subject);
        Assert.Equal("Vevő, Kft. <v@x.example>", email.From);
        Assert.Equal("Kérünk:\n\n- 10 db bot\n- 5 doboz horog", email.BodyText);
        Assert.Equal(["rendeles.pdf"], email.AttachmentNames);
    }

    [Fact]
    public void Html_to_text_keeps_structure()
    {
        var text = HtmlToText.Convert("<div>Első<br>Második</div><script>alert(1)</script><table><tr><td>a</td><td>b</td></tr><tr><td>c</td></tr></table>&lt;ok&gt;");
        Assert.Equal("Első\nMásodik\na b\nc\n<ok>", text.Replace("\t", " ").Replace(" \n", "\n"));
    }
}
