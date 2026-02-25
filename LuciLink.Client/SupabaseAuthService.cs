using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LuciLink.Client;

/// <summary>
/// Supabase 인증 서비스: 로그인, 로그아웃, 세션 관리, 구독 조회, 체험 활성화.
/// supabase-csharp 대신 REST API 직접 호출 (의존성 최소화).
/// </summary>
public class SupabaseAuthService
{
    private static readonly HttpClient Http = new();
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LuciLink", "auth_session.json");

    private AuthSession? _session;

    public bool IsLoggedIn => _session != null && !string.IsNullOrEmpty(_session.AccessToken);
    public string? UserEmail => _session?.User?.Email;
    public string? UserId => _session?.User?.Id;
    public bool IsEmailVerified => _session?.User?.EmailConfirmedAt != null;
    public DateTime? UserCreatedAt => _session?.User?.CreatedAt != null
        ? DateTime.TryParse(_session.User.CreatedAt, out var dt) ? dt : null
        : null;

    /// <summary>이메일/비밀번호 로그인</summary>
    public async Task<AuthResult> SignInAsync(string email, string password)
    {
        var body = JsonSerializer.Serialize(new { email, password });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/auth/v1/token?grant_type=password")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var err = JsonSerializer.Deserialize<AuthError>(json);
                return new AuthResult(false, err?.ErrorDescription ?? err?.Message ?? "로그인 실패");
            }

            _session = JsonSerializer.Deserialize<AuthSession>(json);
            SaveSession();
            return new AuthResult(true, null);
        }
        catch (HttpRequestException ex)
        {
            return new AuthResult(false, $"네트워크 오류: {ex.Message}");
        }
    }

    /// <summary>구독 상태 조회</summary>
    public async Task<SubscriptionInfo?> GetSubscriptionAsync()
    {
        if (_session == null) return null;

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{SupabaseConfig.Url}/rest/v1/user_subscriptions?user_id=eq.{_session.User!.Id}&select=*,products(slug,name,description,price_krw,trial_days)");
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);
        request.Headers.Add("Authorization", $"Bearer {_session.AccessToken}");

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            System.Diagnostics.Debug.WriteLine($"[SUB] HTTP {(int)response.StatusCode}, Body: {json}");

            if (!response.IsSuccessStatusCode) return null;

            var subs = JsonSerializer.Deserialize<SubscriptionInfo[]>(json);
            if (subs != null)
            {
                foreach (var s in subs)
                    System.Diagnostics.Debug.WriteLine($"[SUB] id={s.Id}, status={s.Status}, slug={s.Products?.Slug}");
            }
            // lucilink 또는 lucilink-yearly 모두 매칭
            return subs?.FirstOrDefault(s => s.Products?.Slug != null && s.Products.Slug.StartsWith(SupabaseConfig.ProductSlug));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SUB] Exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>체험 활성화 (pending → trial, 3일 시작) + 기기 핑거프린트 등록</summary>
    public async Task<TrialActivationResult> ActivateTrialAsync(string? deviceHash = null)
    {
        if (_session == null)
            return new TrialActivationResult(false, "not_logged_in", null);

        var bodyObj = new Dictionary<string, object?>
        {
            ["p_product_slug"] = SupabaseConfig.ProductSlug
        };
        if (deviceHash != null)
            bodyObj["p_device_hash"] = deviceHash;

        var body = JsonSerializer.Serialize(bodyObj);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/rest/v1/rpc/activate_trial")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);
        request.Headers.Add("Authorization", $"Bearer {_session.AccessToken}");

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new TrialActivationResult(false, "server_error", null);

            var result = JsonSerializer.Deserialize<TrialRpcResponse>(json);
            if (result == null || !result.Success)
                return new TrialActivationResult(false, result?.Error ?? "unknown", null);

            return new TrialActivationResult(true, result.Status, result.TrialEndDate);
        }
        catch (Exception ex)
        {
            return new TrialActivationResult(false, ex.Message, null);
        }
    }

    /// <summary>이메일/비밀번호 회원가입</summary>
    public async Task<AuthResult> SignUpAsync(string email, string password)
    {
        var body = JsonSerializer.Serialize(new { email, password });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/auth/v1/signup")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var err = JsonSerializer.Deserialize<AuthError>(json);
                return new AuthResult(false, err?.ErrorDescription ?? err?.Message ?? "Sign up failed");
            }

            return new AuthResult(true, null);
        }
        catch (HttpRequestException ex)
        {
            return new AuthResult(false, $"Network error: {ex.Message}");
        }
    }

    /// <summary>비밀번호 재설정 이메일 발송</summary>
    public async Task<AuthResult> ResetPasswordAsync(string email)
    {
        var body = JsonSerializer.Serialize(new { email });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/auth/v1/recover")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);

        try
        {
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var err = JsonSerializer.Deserialize<AuthError>(json);
                return new AuthResult(false, err?.ErrorDescription ?? err?.Message ?? "Reset failed");
            }

            return new AuthResult(true, null);
        }
        catch (HttpRequestException ex)
        {
            return new AuthResult(false, $"Network error: {ex.Message}");
        }
    }

    /// <summary>토큰 갱신 (앱 시작 시 호출)</summary>
    public async Task<bool> RefreshSessionAsync()
    {
        if (_session?.RefreshToken == null) return false;

        var body = JsonSerializer.Serialize(new { refresh_token = _session.RefreshToken });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/auth/v1/token?grant_type=refresh_token")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);

        try
        {
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                ClearSession();
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            _session = JsonSerializer.Deserialize<AuthSession>(json);
            SaveSession();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>로그아웃</summary>
    public async Task SignOutAsync()
    {
        if (_session?.AccessToken != null)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{SupabaseConfig.Url}/auth/v1/logout");
                request.Headers.Add("apikey", SupabaseConfig.AnonKey);
                request.Headers.Add("Authorization", $"Bearer {_session.AccessToken}");
                await Http.SendAsync(request);
            }
            catch { /* 서버 로그아웃 실패해도 로컬은 처리 */ }
        }

        ClearSession();
    }

    /// <summary>저장된 세션 로드 (앱 시작 시)</summary>
    public void LoadSession()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var json = File.ReadAllText(TokenPath);
                _session = JsonSerializer.Deserialize<AuthSession>(json);
            }
        }
        catch { _session = null; }
    }

    private void SaveSession()
    {
        try
        {
            var dir = Path.GetDirectoryName(TokenPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_session, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TokenPath, json);
        }
        catch { /* 세션 저장 실패 무시 */ }
    }

    private void ClearSession()
    {
        _session = null;
        try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { }
    }

    /// <summary>프로그램 첫 로그인 기록 (UPSERT)</summary>
    public async Task RecordProgramLoginAsync()
    {
        if (_session == null) return;

        var body = JsonSerializer.Serialize(new { p_device_info = Environment.MachineName });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/rest/v1/rpc/record_program_login")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);
        request.Headers.Add("Authorization", $"Bearer {_session.AccessToken}");

        try
        {
            await Http.SendAsync(request);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BETA] RecordProgramLogin failed: {ex.Message}");
        }
    }

    /// <summary>피드백 제출</summary>
    public async Task<AuthResult> SubmitFeedbackAsync(string content, string category = "general")
    {
        if (_session == null)
            return new AuthResult(false, "로그인이 필요합니다.");

        var body = JsonSerializer.Serialize(new
        {
            user_id = _session.User!.Id,
            content,
            category
        });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/rest/v1/beta_feedbacks")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);
        request.Headers.Add("Authorization", $"Bearer {_session.AccessToken}");
        request.Headers.Add("Prefer", "return=minimal");

        try
        {
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[BETA] SubmitFeedback failed: {json}");
                return new AuthResult(false, "피드백 제출에 실패했습니다.");
            }
            return new AuthResult(true, null);
        }
        catch (Exception ex)
        {
            return new AuthResult(false, $"네트워크 오류: {ex.Message}");
        }
    }

    /// <summary>베타 테스터 상태 확인 (3가지 조건)</summary>
    public async Task<BetaTesterStatus?> CheckBetaTesterStatusAsync()
    {
        if (_session == null) return null;

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/rest/v1/rpc/check_beta_tester_status")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);
        request.Headers.Add("Authorization", $"Bearer {_session.AccessToken}");

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[BETA] Status: {json}");

            if (!response.IsSuccessStatusCode) return null;

            return JsonSerializer.Deserialize<BetaTesterStatus>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BETA] CheckStatus failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>게스트 모드 사용 여부 확인 (비로그인 — anon 키만 사용)</summary>
    public async Task<bool> CheckGuestUsageAsync(string deviceHash)
    {
        var body = JsonSerializer.Serialize(new { p_device_hash = deviceHash });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/rest/v1/rpc/check_guest_usage")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[GUEST] CheckUsage: {json}");

            if (!response.IsSuccessStatusCode) return false;

            var result = JsonSerializer.Deserialize<GuestUsageResponse>(json);
            return result?.Used ?? false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GUEST] CheckUsage failed: {ex.Message}");
            throw; // 네트워크 오류는 호출자에서 처리
        }
    }

    /// <summary>게스트 모드 사용 기록 (비로그인 — anon 키만 사용)</summary>
    public async Task<bool> RecordGuestUsageAsync(string deviceHash)
    {
        var body = JsonSerializer.Serialize(new { p_device_hash = deviceHash });
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{SupabaseConfig.Url}/rest/v1/rpc/record_guest_usage")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("apikey", SupabaseConfig.AnonKey);

        try
        {
            var response = await Http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[GUEST] RecordUsage: {json}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GUEST] RecordUsage failed: {ex.Message}");
            return false;
        }
    }
}

#region DTOs

public record AuthResult(bool Success, string? Error);
public record TrialActivationResult(bool Success, string? Status, string? TrialEndDate);

public class AuthSession
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("user")]
    public AuthUser? User { get; set; }
}

public class AuthUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("email_confirmed_at")]
    public string? EmailConfirmedAt { get; set; }
}

public class AuthError
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }
}

public class SubscriptionInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trial_end_date")]
    public string? TrialEndDate { get; set; }

    [JsonPropertyName("current_period_end")]
    public string? CurrentPeriodEnd { get; set; }

    [JsonPropertyName("products")]
    public ProductInfo? Products { get; set; }
}

public class ProductInfo
{
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("price_krw")]
    public int PriceKrw { get; set; }

    [JsonPropertyName("trial_days")]
    public int TrialDays { get; set; }
}

public class TrialRpcResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trial_end_date")]
    public string? TrialEndDate { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("already_activated")]
    public bool AlreadyActivated { get; set; }
}

public class BetaTesterStatus
{
    [JsonPropertyName("has_program_login")]
    public bool HasProgramLogin { get; set; }

    [JsonPropertyName("has_approved_feedback")]
    public bool HasApprovedFeedback { get; set; }

    [JsonPropertyName("is_lifetime_eligible")]
    public bool IsLifetimeEligible { get; set; }

    [JsonPropertyName("latest_feedback_status")]
    public string? LatestFeedbackStatus { get; set; }
}

public class GuestUsageResponse
{
    [JsonPropertyName("used")]
    public bool Used { get; set; }

    [JsonPropertyName("used_at")]
    public string? UsedAt { get; set; }
}

#endregion
