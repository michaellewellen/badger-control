using System.Text;

// The super soldier serum, driven by the page's SERUM button.
// Taking it is a GET (the server issues this match's secret string) followed by a POST ("jump on it").
// After that, every command we send carries the string in a header, which is what boosts our soldiers.
public class SerumControl
{
    private const string Url = "api/v1/matches/{0}/superpower/superserum";

    private HttpClient? _http;
    private Guid _matchId;

    public volatile string? Token;      // set only once the serum is really taken
    public volatile string Message = "";

    public void Reset(HttpClient http, Guid matchId)
    {
        _http = http;
        _matchId = matchId;
        Token = null;
        Message = "";
    }

    public async Task<(bool Ok, string Message)> TakeAsync()
    {
        if (_http is null)
        {
            return (false, "No match is running from this page yet.");
        }

        if (Token is not null)
        {
            return (false, "The serum is already taken for this match (you only get one).");
        }

        try
        {
            var url = string.Format(Url, _matchId);
            var token = (await _http.GetStringAsync(url)).Trim().Trim('"');
            using var payload = new StringContent("{\"ifGrenadeThrown\":\"jump on it\"}", Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, payload);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Message = $"Serum refused ({(int)response.StatusCode}): {body}";
                return (false, Message);
            }

            Token = token;
            Message = "Serum taken! Soldiers move and hit twice as hard for about 30 ticks.";
            Console.WriteLine($"SERUM taken: {body}");
            return (true, Message);
        }
        catch (Exception ex)
        {
            Message = "Serum failed: " + ex.Message;
            return (false, Message);
        }
    }
}

// Adds the serum header to command submissions once the serum has been taken.
public class SerumHandler(SerumControl serum) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (serum.Token is { } token
            && request.Method == HttpMethod.Post
            && request.RequestUri?.AbsolutePath.EndsWith("/commands", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.Headers.TryAddWithoutValidation("X-SUPERPOWER-SUPERSERUM", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
