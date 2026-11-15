using Jotdash.Services;

internal static class LinkTests
{
    public static void Run()
    {
        Check(Tasks.Links("").Count == 0 && Tasks.Links("An ordinary note").Count == 0,
            "Plain text remains plain text.");
        var links = Tasks.Links("Read https://example.com/one?q=two&v=3#part\nand http://example.org/next.");
        Check(links.Select(u => u.AbsoluteUri).SequenceEqual(new[]
        {
            "https://example.com/one?q=two&v=3#part", "http://example.org/next",
        }), "Links preserve query strings, fragments and order without sentence punctuation.");
        Check(Tasks.Links("www.example.com/path").Single().AbsoluteUri == "https://www.example.com/path",
            "A www link opens using HTTPS.");
        Check(Tasks.Links("HTTPS://EXAMPLE.COM/ https://example.com/").Count == 1,
            "Repeated equivalent links are shown once.");
        Check(Tasks.Links("(https://example.com/page_(one)).").Single().AbsoluteUri == "https://example.com/page_(one)",
            "Balanced path parentheses survive surrounding prose punctuation.");
        Check(Tasks.Links("[https://example.com/path] and <https://example.org/>").Count == 2,
            "Brackets and angle delimiters are not included in links.");
        Check(Tasks.Links("javascript:alert(1) file:///private intent://example tel:123 data:text/html,hi").Count == 0,
            "Executable, file and arbitrary app schemes are not launchable note links.");
        Check(Tasks.Links("https:// https:/// www. https://user:password@example.com/").Count == 0,
            "Malformed URLs and credential-bearing URLs are excluded.");
        Check(Tasks.Links("<script>alert(1)</script> https://example.com/?x=%3Cscript%3E").Count == 1,
            "Note text is not interpreted as HTML.");
        Check(Tasks.Links("nothttps://example.com/ mail@www.example.com").Count == 0,
            "URL-like substrings within other tokens are not links.");
        Check(Tasks.Links("https://example.com/" + new string('a', 100_000)).Count == 1,
            "Long note links are supported.");
        Check(Tasks.Links("https://example.com/" + new string(')', 100_000)).Single().AbsoluteUri == "https://example.com/",
            "Long runs of unmatched punctuation are stripped in one pass.");
        Console.WriteLine("PASS: note links, safe schemes, URL normalization, punctuation, duplicates and long notes.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
