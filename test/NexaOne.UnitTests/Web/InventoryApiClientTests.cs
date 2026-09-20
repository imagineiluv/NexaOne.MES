using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Web.Services;
using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Auth;

namespace NexaOne.UnitTests.Web;

public sealed class InventoryApiClientTests
{
    private const string WritePath = "api/v1/ivt/shared-equipment/tenant/org/assets";
    private const string WriteOutcome = "The write outcome could not be confirmed. Check the current state before retrying.";
    private const string EquipmentResponse = """
        {"id":"11111111-1111-1111-1111-111111111111",
         "scope":{"productId":"NexaOne.MES","tenantId":"tenant-1","organizationId":"org-1"},
         "version":"22222222-2222-2222-2222-222222222222",
         "code":"EQ-1","name":"공유 장비","capacity":2,"requiresApproval":true,"active":true}
        """;
    private const string ProductPage = """
        {
          "ItEmS": [{
            "ID": "11111111-1111-1111-1111-111111111111",
            "Scope": {"ProductId":"NexaOne.MES","TenantId":"tenant-1","OrganizationId":"org-1"},
            "Version": "22222222-2222-2222-2222-222222222222",
            "Input": {"Code":"P-01","Name":"현재 품명","Translations":[{"Language":"en","Name":"Current name"}]},
            "Active": false
          }],
          "ToTaL": 4294967296
        }
        """;

    [Fact]
    public async Task Product_page_preserves_nested_models_long_total_and_encoded_query_with_authentication()
    {
        var accessToken = Token(DateTime.UtcNow.AddHours(1));
        const string path = "api/v1/ivt/stock/tenant/org/products?text=%20%25_%5BX%5D%20%ED%92%88%EB%AA%85%2F%3F&offset=1&limit=50";
        using var fixture = new ClientFixture(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.Should().Be("https://nexaone.local/mes/" + path);
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be(accessToken);
            request.Headers.GetValues("Accept-Language").Should().Equal("en-US");
            return Response(200, ProductPage);
        });
        await fixture.Tokens.SaveAsync(accessToken, "refresh-test", "operator-test");
        fixture.Ui.Load("EnUs", new Dictionary<string, string>());

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>(path);

        result.StatusCode.Should().Be(200);
        result.Code.Should().BeNull();
        result.Error.Should().BeNull();
        result.Value.Should().NotBeNull();
        result.Value!.Total.Should().Be(4294967296L);
        var product = result.Value.Items.Should().ContainSingle().Which;
        product.Id.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        product.Scope.Should().Be(new BusinessScope("NexaOne.MES", "tenant-1", "org-1"));
        product.Input.Code.Should().Be("P-01");
        product.Input.Name.Should().Be("현재 품명");
        product.Input.Translations.Should().ContainSingle()
            .Which.Should().Be(new ProductTranslation("en", "Current name"));
        product.Active.Should().BeFalse();
        fixture.RequestCount.Should().Be(1);
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CheckedOut", EquipmentBookingState.CheckedOut)]
    [InlineData("approved", EquipmentBookingState.Approved)]
    [InlineData("RETURNED", EquipmentBookingState.Returned)]
    public async Task Booking_page_reads_server_string_enum_states(string state, EquipmentBookingState expected)
    {
        var body = """
            {"items":[{
              "id":"11111111-1111-1111-1111-111111111111",
              "scope":{"productId":"NexaOne.MES","tenantId":"tenant-1","organizationId":"org-1"},
              "version":"22222222-2222-2222-2222-222222222222",
              "equipmentId":"33333333-3333-3333-3333-333333333333",
              "employeeId":"44444444-4444-4444-4444-444444444444",
              "start":"2026-09-20T01:00:00+00:00","end":"2026-09-20T02:00:00+00:00",
              "quantity":1,"requestedBy":"operator","state":"$STATE"
            }],"total":1}
            """.Replace("$STATE", state, StringComparison.Ordinal);
        using var fixture = new ClientFixture(_ => Response(200, body));

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<EquipmentBooking>>(
            "api/v1/ivt/shared-equipment/tenant/org/bookings");

        result.Error.Should().BeNull();
        result.Value!.Items.Should().ContainSingle().Which.State.Should().Be(expected);
        result.Value.Total.Should().Be(1);
    }

    [Fact]
    public async Task Valid_empty_page_is_a_success()
    {
        using var fixture = new ClientFixture(request =>
        {
            request.Headers.GetValues("Accept-Language").Should().Equal("ko-KR");
            return Response(200, "{\"items\":[],\"total\":0}");
        });

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/scopes/me");

        result.StatusCode.Should().Be(200);
        result.Value!.Items.Should().BeEmpty();
        result.Value.Total.Should().Be(0);
        result.Code.Should().BeNull();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(403, "{\"code\":\"FORBIDDEN\",\"description\":\"Read grant revoked\"}", "FORBIDDEN", "Read grant revoked")]
    [InlineData(404, "{\"CODE\":\"SCOPE_NOT_FOUND\"}", "SCOPE_NOT_FOUND", "SCOPE_NOT_FOUND")]
    [InlineData(409, "{\"title\":\"Conflict\",\"detail\":\"Scope changed\",\"status\":500,\"code\":\"SCOPE_CONFLICT\"}", "SCOPE_CONFLICT", "Scope changed")]
    [InlineData(503, "{\"Title\":\"Module unavailable\",\"status\":503}", null, "Module unavailable")]
    [InlineData(400, "{\"message\":\"Invalid offset\"}", null, "Invalid offset")]
    public async Task Business_errors_and_problem_details_keep_status_code_and_reason_without_global_toast(
        int status, string body, string? expectedCode, string expectedError)
    {
        using var fixture = new ClientFixture(_ => Response(status, body));

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/stock/tenant/org/products");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(status);
        result.Code.Should().Be(expectedCode);
        result.Error.Should().Be(expectedError);
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData(403, "")]
    [InlineData(503, "<html>Unavailable</html>")]
    [InlineData(500, "null")]
    [InlineData(502, "[]")]
    [InlineData(400, "{\"code\":42,\"description\":false}")]
    public async Task Unreadable_error_body_retains_http_failure(int status, string body)
    {
        using var fixture = new ClientFixture(_ => Response(status, body));

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/stock/tenant/org/products");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(status);
        result.Code.Should().BeNull();
        result.Error.Should().Contain(status.ToString());
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(200, "null")]
    [InlineData(200, "{\"items\":")]
    [InlineData(200, "[]")]
    [InlineData(200, "{\"items\":[],\"total\":9223372036854775808}")]
    [InlineData(204, "")]
    public async Task Malformed_or_null_success_is_an_explicit_failure_with_original_status(int status, string body)
    {
        using var fixture = new ClientFixture(_ => Response(status, body));

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/stock/tenant/org/products");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(status);
        result.Code.Should().Be("INVALID_INVENTORY_RESPONSE");
        result.Error.Should().NotBeNullOrWhiteSpace();
        fixture.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_domain_constructor_values_are_response_failures()
    {
        using var fixture = new ClientFixture(_ => Response(200,
            ProductPage.Replace("\"tenant-1\"", "\"\"", StringComparison.Ordinal)));

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/stock/tenant/org/products");

        result.StatusCode.Should().Be(200);
        result.Value.Should().BeNull();
        result.Code.Should().Be("INVALID_INVENTORY_RESPONSE");
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unknown_string_enum_is_a_response_failure()
    {
        using var fixture = new ClientFixture(_ => Response(200, "[\"UnexpectedState\"]"));

        var result = await fixture.Client.ReadInventoryAsync<EquipmentBookingState[]>("api/v1/ivt/scopes/me");

        result.Value.Should().BeNull();
        result.Code.Should().Be("INVALID_INVENTORY_RESPONSE");
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://outside.invalid/api/v1/ivt/scopes/me")]
    [InlineData("//outside.invalid/api/v1/ivt/scopes/me")]
    [InlineData("\\\\outside.invalid\\api\\v1\\ivt\\scopes\\me")]
    [InlineData("/api/v1/ivt/scopes/me")]
    [InlineData("api/v1/ivt-other/scopes/me")]
    [InlineData("api/v1/ivt/../../auth/me")]
    [InlineData("api/v1/ivt/%2e%2E/%2e%2e/auth/me")]
    [InlineData("api/v1/ivt/scopes%2f..%2f..%2fauth/me")]
    [InlineData("api/v1/ivt/%252e%252e/auth/me")]
    [InlineData("api/v1/ivt/scopes\\..\\..\\auth/me")]
    [InlineData("api/v1/ivt/scopes%5c..%5c..%5cauth/me")]
    [InlineData("api/v1/ivt/scopes/%00/me")]
    [InlineData("api/v1/ivt/scopes/me#fragment")]
    [InlineData("api/v1/ivt/scopes/me\r\n")]
    public async Task Invalid_paths_are_rejected_before_token_access_or_any_http_request(string? path)
    {
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not send credentials"));
        await fixture.Tokens.SaveAsync(Token(DateTime.UtcNow.AddHours(-1)), "refresh-test", "operator-test");
        var storageCalls = fixture.Storage.InvocationCount;

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>(path!);
        var write = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, path!, new { }, "operator-a");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(400);
        result.Code.Should().Be("INVALID_INVENTORY_PATH");
        result.Error.Should().NotBeNullOrWhiteSpace();
        write.Value.Should().BeNull();
        write.StatusCode.Should().Be(400);
        write.Code.Should().Be("INVALID_INVENTORY_PATH");
        write.Error.Should().NotBeNullOrWhiteSpace();
        fixture.Storage.InvocationCount.Should().Be(storageCalls);
        fixture.RequestCount.Should().Be(0);
        fixture.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_access_token_uses_existing_refresh_and_sends_new_token_to_inventory()
    {
        var oldToken = Token(DateTime.UtcNow.AddHours(-1));
        var newToken = Token(DateTime.UtcNow.AddHours(1));
        var paths = new List<string>();
        using var fixture = new ClientFixture(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/mes/api/v1/auth/refresh")
            {
                request.Method.Should().Be(HttpMethod.Post);
                request.Headers.Authorization!.Parameter.Should().Be(oldToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = newToken, refreshToken = "refreshed-test" })
                };
            }
            request.Method.Should().Be(HttpMethod.Get);
            request.Headers.Authorization!.Parameter.Should().Be(newToken);
            return Response(200, "{\"items\":[],\"total\":0}");
        });
        await fixture.Tokens.SaveAsync(oldToken, "refresh-test", "operator-test");

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/scopes/me");

        result.Error.Should().BeNull();
        paths.Should().Equal("/mes/api/v1/auth/refresh", "/mes/api/v1/ivt/scopes/me");
        (await fixture.Tokens.GetAccessTokenAsync()).Should().Be(newToken);
        (await fixture.Tokens.GetRefreshTokenAsync()).Should().Be("refreshed-test");
    }

    [Fact]
    public async Task Persistent_unauthorized_response_survives_existing_single_retry()
    {
        using var fixture = new ClientFixture(_ => Response(401, "{\"code\":\"UNAUTHORIZED\"}"));
        await fixture.Tokens.SaveAsync(Token(DateTime.UtcNow.AddHours(1)), "refresh-test", "operator-test");

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/scopes/me");

        fixture.RequestCount.Should().Be(2);
        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(401);
        result.Code.Should().Be("UNAUTHORIZED");
        result.Error.Should().Be("UNAUTHORIZED");
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_failure_or_timeout_keeps_existing_503_mapping(bool timeout)
    {
        using var fixture = new ClientFixture(_ =>
        {
            if (timeout) throw new TaskCanceledException("Simulated transport timeout");
            throw new HttpRequestException("Simulated offline transport");
        });
        fixture.Ui.Load("EnUs", new Dictionary<string, string> { ["error.unreachable"] = "Server unreachable" });

        var result = await fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/scopes/me");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(503);
        result.Code.Should().Be("SERVER_UNREACHABLE");
        result.Error.Should().Be("Server unreachable");
        fixture.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Requested_cancellation_during_send_propagates()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ClientFixture(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(200, "{\"items\":[],\"total\":0}");
        });

        var pending = fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/scopes/me", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        fixture.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_access_tokens_or_send()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not send"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.ReadInventoryAsync<BusinessPage<Product>>("api/v1/ivt/scopes/me", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, "operator-a", cancellation.Token));

        fixture.Storage.InvocationCount.Should().Be(0);
        fixture.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Write_sends_exact_method_body_path_token_and_language(string method)
    {
        var token = Token(DateTime.UtcNow.AddHours(1), "operator-a");
        var operationId = Guid.NewGuid();
        const string path = WritePath + "?text=%20%25_%5B%23%5D";
        using var fixture = new ClientFixture(async (request, ct) =>
        {
            request.Method.Method.Should().Be(method);
            request.RequestUri!.AbsoluteUri.Should().Be("https://nexaone.local/mes/" + path);
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be(token);
            request.Headers.GetValues("Accept-Language").Should().Equal("en-US");
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/json");
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            json.RootElement.GetProperty("operationId").GetGuid().Should().Be(operationId);
            json.RootElement.GetProperty("name").GetString().Should().Be("공유 장비 %_[x]");
            json.RootElement.GetProperty("capacity").GetInt32().Should().Be(2);
            json.RootElement.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
            return Response(201, EquipmentResponse);
        });
        await fixture.Tokens.SaveAsync(token, "refresh-test", "operator-a");
        fixture.Ui.Load("EnUs", new Dictionary<string, string>());

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(new HttpMethod(method), path,
            new { OperationId = operationId, Code = "EQ-1", Name = "공유 장비 %_[x]", Capacity = 2, RequiresApproval = true }, "operator-a");

        result.StatusCode.Should().Be(201);
        result.Value!.Name.Should().Be("공유 장비");
        result.Value.Scope.Should().Be(new BusinessScope("NexaOne.MES", "tenant-1", "org-1"));
        result.Code.Should().BeNull();
        result.Error.Should().BeNull();
        fixture.RequestCount.Should().Be(1);
        fixture.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_reads_booking_string_enum_with_existing_domain_options()
    {
        const string booking = """
            {"id":"11111111-1111-1111-1111-111111111111",
             "scope":{"productId":"NexaOne.MES","tenantId":"tenant-1","organizationId":"org-1"},
             "version":"22222222-2222-2222-2222-222222222222",
             "equipmentId":"33333333-3333-3333-3333-333333333333",
             "employeeId":"44444444-4444-4444-4444-444444444444",
             "start":"2026-09-20T01:00:00+00:00","end":"2026-09-20T02:00:00+00:00",
             "quantity":1,"requestedBy":"operator-a","STATE":"CheckedOut"}
            """;
        using var fixture = new ClientFixture(_ => Response(200, booking));
        await SignIn(fixture);

        var result = await fixture.Client.WriteInventoryAsync<EquipmentBooking>(HttpMethod.Post,
            "api/v1/ivt/shared-equipment/tenant/org/bookings/11111111-1111-1111-1111-111111111111/check-out",
            new { Version = Guid.NewGuid() }, "operator-a");

        result.Value!.State.Should().Be(EquipmentBookingState.CheckedOut);
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Write_rejects_unsupported_methods_before_token_access(string? method)
    {
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not send"));

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(
            method is null ? null! : new HttpMethod(method), WritePath, new { }, "operator-a");

        result.StatusCode.Should().Be(400);
        result.Code.Should().Be("INVALID_INVENTORY_REQUEST");
        fixture.Storage.InvocationCount.Should().Be(0);
        fixture.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Write_rejects_null_body_before_token_access()
    {
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not send"));

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Put, WritePath, null!, "operator-a");

        result.StatusCode.Should().Be(400);
        result.Code.Should().Be("INVALID_INVENTORY_REQUEST");
        fixture.Storage.InvocationCount.Should().Be(0);
        fixture.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" operator-a")]
    [InlineData("operator-a ")]
    [InlineData("operator\na")]
    [InlineData("012345678901234567890123456789012345678901234567890")]
    public async Task Write_rejects_invalid_expected_identity_before_token_access(string? userId)
    {
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not send"));

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, userId!);

        result.StatusCode.Should().Be(400);
        result.Code.Should().Be("INVALID_INVENTORY_REQUEST");
        fixture.Storage.InvocationCount.Should().Be(0);
        fixture.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("operator-b", null)]
    [InlineData("OPERATOR-A", null)]
    [InlineData("operator-a", "operator-b")]
    [InlineData("operator-a", "")]
    public async Task Write_rejects_missing_or_changed_selected_token_identity_without_sending(string? subject, string? nameIdentifier)
    {
        using var fixture = new ClientFixture(_ => throw new InvalidOperationException("Must not send"));
        if (subject is not null || nameIdentifier is not null)
            await fixture.Tokens.SaveAsync(Token(DateTime.UtcNow.AddHours(1), subject, nameIdentifier), "refresh-test", "operator-a");

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, "operator-a");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(401);
        result.Code.Should().Be("INVENTORY_IDENTITY_CHANGED");
        result.Error.Should().NotBeNullOrWhiteSpace();
        fixture.RequestCount.Should().Be(0);
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData("operator-a", null)]
    [InlineData("ignored-sub", "operator-a")]
    public async Task Write_uses_NameIdentifier_before_raw_subject(string subject, string? nameIdentifier)
    {
        using var fixture = new ClientFixture(_ => Response(200, EquipmentResponse));
        await fixture.Tokens.SaveAsync(Token(DateTime.UtcNow.AddHours(1), subject, nameIdentifier), "refresh-test", "operator-a");

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Put, WritePath, new { }, "operator-a");

        result.Value.Should().NotBeNull();
        result.Error.Should().BeNull();
        fixture.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData("POST", "operator-a", true)]
    [InlineData("PUT", "operator-a", true)]
    [InlineData("POST", "operator-b", false)]
    [InlineData("PUT", "operator-b", false)]
    [InlineData("POST", null, false)]
    [InlineData("PUT", null, false)]
    public async Task Write_401_refresh_replays_same_payload_only_for_original_identity(string method, string? refreshedUser, bool replay)
    {
        var originalToken = Token(DateTime.UtcNow.AddHours(1), "operator-a");
        var refreshedToken = Token(DateTime.UtcNow.AddHours(2), refreshedUser);
        var bodies = new List<string>();
        var tokens = new List<string?>();
        var refreshCount = 0;
        AuthTokenService? storageTokens = null;
        using var fixture = new ClientFixture(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/mes/api/v1/auth/refresh")
            {
                refreshCount++;
                request.Method.Should().Be(HttpMethod.Post);
                if (refreshedUser is null) return Response(401, "{\"code\":\"REFRESH_REVOKED\"}");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accessToken = refreshedToken, refreshToken = "refreshed-test" })
                };
            }
            request.Method.Method.Should().Be(method);
            request.RequestUri.AbsolutePath.Should().Be("/mes/" + WritePath);
            bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            tokens.Add(request.Headers.Authorization!.Parameter);
            if (bodies.Count == 1)
            {
                // Force refresh without clock-dependent waits; the first request already owns token A.
                await storageTokens!.SaveAsync(Token(DateTime.UtcNow.AddHours(-1), "operator-a"), "refresh-test", "operator-a");
                return Response(401, "{\"code\":\"TOKEN_EXPIRED\"}");
            }
            return Response(200, EquipmentResponse);
        });
        storageTokens = fixture.Tokens;
        await fixture.Tokens.SaveAsync(originalToken, "refresh-test", "operator-a");
        var operationId = Guid.NewGuid();
        var body = new { OperationId = operationId, Code = "EQ-1", Name = "Original name", Capacity = 2, RequiresApproval = true };

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(new HttpMethod(method), WritePath, body, "operator-a");

        refreshCount.Should().Be(1);
        bodies.Should().HaveCount(replay ? 2 : 1);
        using var sent = JsonDocument.Parse(bodies[0]);
        sent.RootElement.GetProperty("operationId").GetGuid().Should().Be(operationId);
        tokens[0].Should().Be(originalToken);
        if (replay)
        {
            bodies[1].Should().Be(bodies[0]);
            tokens[1].Should().Be(refreshedToken);
            result.Value.Should().NotBeNull();
            result.Error.Should().BeNull();
        }
        else
        {
            result.Value.Should().BeNull();
            result.StatusCode.Should().Be(401);
            result.Code.Should().Be("INVENTORY_IDENTITY_CHANGED");
        }
        fixture.RequestCount.Should().Be(replay ? 3 : 2);
    }

    [Fact]
    public async Task Write_persistent_unauthorized_response_stops_after_one_retry()
    {
        using var fixture = new ClientFixture(_ => Response(401, "{\"code\":\"UNAUTHORIZED\"}"));
        await SignIn(fixture);

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, "operator-a");

        fixture.RequestCount.Should().Be(2);
        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(401);
        result.Code.Should().Be("UNAUTHORIZED");
        result.Error.Should().Be("UNAUTHORIZED");
        fixture.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_already_sent_as_A_can_finish_after_storage_changes_to_B()
    {
        var tokenA = Token(DateTime.UtcNow.AddHours(1), "operator-a");
        var tokenB = Token(DateTime.UtcNow.AddHours(1), "operator-b");
        AuthTokenService? storageTokens = null;
        using var fixture = new ClientFixture(async (request, _) =>
        {
            request.Headers.Authorization!.Parameter.Should().Be(tokenA);
            await storageTokens!.SaveAsync(tokenB, "refresh-b", "operator-b");
            return Response(200, EquipmentResponse);
        });
        storageTokens = fixture.Tokens;
        await fixture.Tokens.SaveAsync(tokenA, "refresh-a", "operator-a");

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, "operator-a");

        result.Value.Should().NotBeNull();
        result.Error.Should().BeNull();
        (await fixture.Tokens.GetAccessTokenAsync()).Should().Be(tokenB);
        fixture.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData(400, "{\"code\":\"INVALID_EQUIPMENT\"}", "INVALID_EQUIPMENT", "INVALID_EQUIPMENT")]
    [InlineData(403, "{\"code\":\"BUSINESS_ACCESS_DENIED\",\"description\":\"Grant revoked\"}", "BUSINESS_ACCESS_DENIED", "Grant revoked")]
    [InlineData(409, "{\"code\":\"BUSINESS_VERSION_CONFLICT\"}", "BUSINESS_VERSION_CONFLICT", "BUSINESS_VERSION_CONFLICT")]
    [InlineData(503, "{\"status\":500,\"title\":\"Unavailable\",\"detail\":\"Read the asset before retrying\"}", null, "Read the asset before retrying")]
    [InlineData(503, "<html>internal detail</html>", null, WriteOutcome)]
    public async Task Write_preserves_rejections_and_problem_details_without_replay(int status, string response, string? code, string error)
    {
        using var fixture = new ClientFixture(_ => Response(status, response));
        await SignIn(fixture);

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, "operator-a");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(status);
        result.Code.Should().Be(code);
        result.Error.Should().Be(error);
        fixture.RequestCount.Should().Be(1);
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(200, "null")]
    [InlineData(200, "{\"private\":\"internal secret\"")]
    [InlineData(200, "[]")]
    [InlineData(204, "")]
    public async Task Write_unreadable_success_is_uncertain_without_replay_or_raw_body(int status, string response)
    {
        using var fixture = new ClientFixture(_ => Response(status, response));
        await SignIn(fixture);

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Put, WritePath, new { }, "operator-a");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(status);
        result.Code.Should().Be("INVALID_INVENTORY_RESPONSE");
        result.Error.Should().Be(WriteOutcome);
        fixture.RequestCount.Should().Be(1);
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData("connection", "SERVER_UNREACHABLE")]
    [InlineData("timeout", "SERVER_UNREACHABLE")]
    [InlineData("io", "INVENTORY_RESPONSE_UNAVAILABLE")]
    [InlineData("body", "SERVER_UNREACHABLE")]
    public async Task Write_transport_or_body_connection_failure_is_uncertain_and_never_replayed(string failure, string code)
    {
        using var fixture = new ClientFixture(_ =>
        {
            if (failure == "connection") throw new HttpRequestException("internal secret");
            if (failure == "timeout") throw new TaskCanceledException("internal secret");
            if (failure == "io") throw new IOException("internal secret");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CallbackContent(async (stream, ct) =>
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"private\":\"internal secret\""), ct);
                    throw new IOException("internal connection detail");
                })
            };
        });
        await SignIn(fixture);

        var result = await fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Post, WritePath, new { }, "operator-a");

        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(503);
        result.Code.Should().Be(code);
        result.Error.Should().Be(WriteOutcome);
        fixture.RequestCount.Should().Be(1);
        fixture.Notifications.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_caller_cancellation_during_send_or_content_read_propagates(bool readingContent)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        async Task WaitForCancellation(CancellationToken ct)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        using var fixture = new ClientFixture(async (_, ct) =>
        {
            if (!readingContent) await WaitForCancellation(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CallbackContent((_, contentToken) => WaitForCancellation(contentToken))
            };
        });
        await SignIn(fixture);

        var pending = fixture.Client.WriteInventoryAsync<SharedEquipment>(HttpMethod.Put, WritePath, new { }, "operator-a", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        fixture.RequestCount.Should().Be(1);
        fixture.Notifications.Should().BeEmpty();
    }

    private static Task SignIn(ClientFixture fixture)
    {
        fixture.Ui.Load("EnUs", new Dictionary<string, string> { ["error.inventoryWriteOutcome"] = WriteOutcome });
        return fixture.Tokens.SaveAsync(Token(DateTime.UtcNow.AddHours(1), "operator-a"), "refresh-test", "operator-a");
    }

    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string Token(DateTime expires, string? subject = null, string? nameIdentifier = null)
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim("sub", subject));
        if (nameIdentifier is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: claims, notBefore: expires.AddHours(-2), expires: expires));
    }

    private sealed class CallbackContent(Func<Stream, CancellationToken, Task> serialize) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => serialize(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => serialize(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ClientFixture : IDisposable
    {
        private readonly HttpClient _http;
        public SessionStorageJs Storage { get; } = new();
        public AuthTokenService Tokens { get; }
        public UiTextService Ui { get; } = new();
        public List<ApiNotification> Notifications { get; } = [];
        public ApiClient Client { get; }
        public int RequestCount { get; private set; }

        public ClientFixture(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }

        public ClientFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _http = new HttpClient(new Handler((request, token) =>
            {
                RequestCount++;
                return respond(request, token);
            })) { BaseAddress = new Uri("https://nexaone.local/mes/") };
            Tokens = new AuthTokenService(new ProtectedSessionStorage(Storage, new EphemeralDataProtectionProvider()));
            var notifier = new ApiNotificationService();
            notifier.OnNotify += Notifications.Add;
            Client = new ApiClient(_http, Tokens, new JwtAuthStateProvider(Tokens), notifier, Ui);
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private sealed class SessionStorageJs : IJSRuntime
    {
        private readonly Dictionary<string, string> _values = new();
        public int InvocationCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            var key = (string)args![0]!;
            switch (identifier)
            {
                case "sessionStorage.getItem":
                    return ValueTask.FromResult(_values.TryGetValue(key, out var value)
                        ? (TValue)(object)value : default(TValue)!);
                case "sessionStorage.setItem":
                    _values[key] = (string)args[1]!;
                    break;
                case "sessionStorage.removeItem":
                    _values.Remove(key);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected browser storage operation: " + identifier);
            }
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
