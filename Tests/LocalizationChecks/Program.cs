using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using PlayerApp;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }
    checks++;
}

var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "PlayerApp", "PlayerApp.csproj")))
{
    root = root.Parent;
}
if (root is null)
{
    throw new InvalidOperationException("Run these checks from within this repository.");
}
var appPath = Path.Combine(root.FullName, "PlayerApp");
Dictionary<string, string> ReadResource(string name) => XDocument.Load(Path.Combine(appPath, "Resources", name))
    .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
var english = ReadResource("UiStrings.resx");
var japanese = ReadResource("UiStrings.ja.resx");
Check(english.Keys.Order().SequenceEqual(japanese.Keys.Order()), "Both languages define identical keys");
foreach (var key in english.Keys)
{
    Check(!string.IsNullOrWhiteSpace(english[key]) && !string.IsNullOrWhiteSpace(japanese[key]), $"Nonempty resource: {key}");
    var en = CompositeFormat.Parse(english[key]);
    var ja = CompositeFormat.Parse(japanese[key]);
    Check(en.MinimumArgumentCount == ja.MinimumArgumentCount, $"Matching format arguments: {key}");
    var arguments = Enumerable.Repeat<object>(3, en.MinimumArgumentCount).ToArray();
    _ = string.Format(CultureInfo.InvariantCulture, english[key], arguments);
    _ = string.Format(CultureInfo.InvariantCulture, japanese[key], arguments);
}

var accessors = typeof(UiText).GetProperties(BindingFlags.Public | BindingFlags.Static);
Check(accessors.Select(p => p.Name).Order().SequenceEqual(english.Keys.Order()), "Typed accessors match all resource keys");
var osCulture = CultureInfo.CurrentUICulture;
Check(UiText.LyricsTitle == (osCulture.TwoLetterISOLanguageName == "ja" ? "歌詞" : "Lyrics"), "Default UI culture follows Windows display language");
var originalCulture = CultureInfo.CurrentCulture;
try
{
    foreach (var locale in new[] { "ja", "ja-JP", "en", "en-US", "en-GB", "fr-FR", "de-DE" })
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
        // The user's numeric region can differ from the display language.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var expected = locale.StartsWith("ja", StringComparison.Ordinal) ? japanese : english;
        foreach (var accessor in accessors)
        {
            Check((string)accessor.GetValue(null)! == expected[accessor.Name], $"Resource resolution: {locale}/{accessor.Name}");
        }
        Check(await Task.Run(() => UiText.LyricsTitle) == expected["LyricsTitle"], $"UI language flows through async operations: {locale}");
        Check(UiText.Format(UiText.VolumePercent, 37) == "37%", $"Volume formatting: {locale}");
        Check(UiText.Format(UiText.BatteryDescription, 37).Contains("37%", StringComparison.Ordinal), $"Battery formatting: {locale}");
        Check(UiText.Format(UiText.SeekTo, "1:23").Contains("1:23", StringComparison.Ordinal), $"Seek time preserved: {locale}");
        Check(UiText.Format(UiText.AudioActionName, UiText.Mute, "Noble FoKus Rex5").Contains("Noble FoKus Rex5", StringComparison.Ordinal), $"Device name preserved: {locale}");
    }
    CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    Check(UiText.LyricsTitle == "Lyrics", "Neutral resource is English");
    var missingKeyRejected = false;
    try
    {
        _ = UiText.Get("NonexistentResource");
    }
    catch (InvalidOperationException)
    {
        missingKeyRejected = true;
    }
    Check(missingKeyRejected, "Missing keys fail explicitly");
}
finally
{
    CultureInfo.CurrentUICulture = osCulture;
    CultureInfo.CurrentCulture = originalCulture;
}

var xaml = XDocument.Load(Path.Combine(appPath, "MainWindow.xaml"));
foreach (var attribute in xaml.Descendants().Attributes())
{
    if (attribute.Value.StartsWith("{local:Localize Key=", StringComparison.Ordinal))
    {
        var key = attribute.Value[20..^1];
        Check(english.ContainsKey(key), $"XAML resource exists: {key}");
    }
    if (attribute.Name.LocalName is "Text" or "Content" or "ToolTipService.ToolTip" or "AutomationProperties.Name")
    {
        Check(attribute.Value.StartsWith('{') || !attribute.Value.Any(char.IsLetter), $"No hardcoded XAML labels: {attribute.Name}");
    }
}
foreach (var path in Directory.EnumerateFiles(appPath, "MainWindow*.cs"))
{
    var source = File.ReadAllText(path);
    foreach (Match match in Regex.Matches(source, @"UiText\.([A-Z]\w+)"))
    {
        var key = match.Groups[1].Value;
        Check(key is "Format" or "Get" || english.ContainsKey(key), $"Code resource exists: {key}");
    }
    Check(!Regex.IsMatch(source, "[ぁ-んァ-ヶ一-龠]"), $"Japanese UI strings moved to resources: {Path.GetFileName(path)}");
}

if (args is ["--app-assembly", var assemblyPath])
{
    // Inspect compiled production resources without creating any WinUI objects.
    var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
    var manager = new ResourceManager("PlayerApp.Resources.UiStrings", assembly);
    foreach (var locale in new[] { "ja-JP", "en-US", "en-GB", "fr-FR" })
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var expected = locale.StartsWith("ja", StringComparison.Ordinal) ? japanese : english;
        foreach (var key in english.Keys)
        {
            Check(manager.GetString(key, culture) == expected[key], $"Compiled app resource: {locale}/{key}");
        }
    }
}
Console.WriteLine($"PASS: {checks} localization checks; OS UI language: {osCulture.Name}");
