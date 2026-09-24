using System.Text.Json;
using RemoteOpsTool.Helpers;
using RemoteOpsTool.Models;
using RemoteOpsTool.Services;
using RemoteOpsTool.ViewModels;

namespace RemoteOpsTool.Tests;

public class CredentialHealthTests
{
    [Fact]
    public void RemoteErrorClassifier_PreservesLocalStartFailureDetail()
    {
        var result = RemoteErrorClassifier.Explain("boom", -1);

        Assert.Equal("本地启动失败：boom", result);
        Assert.DoesNotContain("Unknown error (0xffffffff)", result);
    }

    [Fact]
    public void RemoteErrorClassifier_DoesNotUseWin32ExceptionForMissingMinusOneDetail()
    {
        var result = RemoteErrorClassifier.Explain(null, -1);

        Assert.Equal("本地进程启动失败，但未返回详细错误。", result);
        Assert.DoesNotContain("Unknown error (0xffffffff)", result);
    }

    [Fact]
    public void CredentialInfo_DeserializesLegacySelectionAsUnverified()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                UserName = @"CONTOSO\opsuser",
                EncryptedPassword = "abc",
                IsSelected = true,
            },
        });

        var credential = Assert.Single(JsonSerializer.Deserialize<List<CredentialInfo>>(json)!);

        Assert.Equal(@"CONTOSO\opsuser", credential.UserName);
        Assert.True(credential.IsSelected);
        Assert.Equal(CredentialHealth.Unverified, credential.Health);
    }

    [Fact]
    public void CredentialInfo_ValidatesHostCaseInsensitivelyAndTrimsWhitespace()
    {
        var credential = new CredentialInfo
        {
            LastValidatedAt = DateTimeOffset.Now,
            LastValidatedHost = " HOST01 ",
        };

        Assert.True(credential.IsValidatedForHost("HOST01"));
        Assert.False(credential.IsValidatedForHost("other-host"));
    }

    [Fact]
    public void CredentialInfo_SecretUnreadableIsAlwaysVisibleForCurrentHost()
    {
        var credential = new CredentialInfo
        {
            Health = CredentialHealth.SecretUnreadable,
            LastValidatedAt = DateTimeOffset.Now,
            LastValidatedHost = "other-host",
        };

        Assert.Equal(CredentialHealth.SecretUnreadable, credential.GetHealthForHost("HOST01"));
    }

    [Fact]
    public void CredentialHealthClassifier_ReturnsHealthyWhenAnyCommandChannelSucceeds()
    {
        var assessment = CredentialHealthClassifier.Classify(
        [
            Failure("WMI/DCOM", "Access is denied."),
            Success("PsExec 临时执行"),
            Failure("计划任务 RPC", "Access is denied."),
        ]);

        Assert.Equal(CredentialHealth.Healthy, assessment.Health);
        Assert.Empty(assessment.Error);
    }

    [Fact]
    public void CredentialHealthClassifier_ClassifiesSmbSessionConflict()
    {
        var assessment = CredentialHealthClassifier.Classify(
        [
            Failure("ADMIN$ 管理共享", "System error 1219 has occurred. Multiple connections to a server are not allowed."),
        ]);

        Assert.Equal(CredentialHealth.SessionConflict, assessment.Health);
        Assert.Contains("1219", assessment.Error);
    }

    [Fact]
    public void CredentialHealthClassifier_ClassifiesAuthorizationDenied()
    {
        var assessment = CredentialHealthClassifier.Classify(
        [
            Failure("WMI/DCOM", "Access is denied."),
        ]);

        Assert.Equal(CredentialHealth.AuthorizationDenied, assessment.Health);
    }

    [Fact]
    public void CredentialHealthClassifier_ClassifiesLegacyUnknownErrorAsLocalLogonBlocked()
    {
        var assessment = CredentialHealthClassifier.Classify(
        [
            Failure("PsExec 临时执行", "Unknown error (0xffffffff)"),
        ]);

        Assert.Equal(CredentialHealth.LocalLogonBlocked, assessment.Health);
    }

    [Fact]
    public void CredentialHealthClassifier_DoesNotTreatPort5986AsPasswordError()
    {
        var assessment = CredentialHealthClassifier.Classify(
        [
            Failure("PsExec 临时执行", "Port 5986 unreachable"),
        ]);

        Assert.Equal(CredentialHealth.TransportUnavailable, assessment.Health);
    }

    [Fact]
    public async Task CredentialService_LoadAsync_MarksUnreadableSecret()
    {
        var path = CreateTempCredentialPath();
        try
        {
            var json = JsonSerializer.Serialize(new[]
            {
                new
                {
                    UserName = @"CONTOSO\opsuser",
                    EncryptedPassword = "not-base64",
                    IsSelected = true,
                },
            });
            await File.WriteAllTextAsync(path, json);

            var service = new CredentialService(new TestLogService(), path);
            await service.LoadAsync();

            var credential = Assert.Single(service.Credentials);
            Assert.Same(credential, service.SelectedCredential);
            Assert.Equal(CredentialHealth.SecretUnreadable, credential.Health);
            Assert.Contains("无法解密", credential.LastError);
        }
        finally
        {
            await DeleteTempCredentialPathAsync(path);
        }
    }

    [Fact]
    public async Task CredentialService_SetSelectedCredential_EnforcesSingleCurrentCredential()
    {
        var path = CreateTempCredentialPath();
        try
        {
            var first = new CredentialInfo { UserName = @"CONTOSO\first" };
            var second = new CredentialInfo { UserName = @"CONTOSO\second" };
            var service = new CredentialService(new TestLogService(), path);

            service.Add(first);
            service.Add(second);
            service.SetSelectedCredential(first);

            Assert.Same(first, service.SelectedCredential);
            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.Single(service.GetSelectedCredentials());
        }
        finally
        {
            await DeleteTempCredentialPathAsync(path);
        }
    }

    [Fact]
    public async Task CredentialService_TryDecryptPassword_RestoresUnverifiedState()
    {
        var path = CreateTempCredentialPath();
        try
        {
            var encryptedPassword = CredentialService.EncryptPassword("P@ssw0rd!");
            var credential = new CredentialInfo
            {
                UserName = @"CONTOSO\opsuser",
                EncryptedPassword = encryptedPassword,
                Health = CredentialHealth.SecretUnreadable,
                LastError = "old error",
            };
            var service = new CredentialService(new TestLogService(), path);

            Assert.True(service.TryDecryptPassword(credential, out var password));

            Assert.Equal("P@ssw0rd!", password);
            Assert.Equal(CredentialHealth.Unverified, credential.Health);
            Assert.Equal(string.Empty, credential.LastError);
            Assert.NotNull(credential.LastUsedAt);
        }
        finally
        {
            await DeleteTempCredentialPathAsync(path);
        }
    }

    [Fact]
    public void CredentialViewModel_ListSelectionDoesNotChangeCurrentCredentialUntilConfirmed()
    {
        var service = new TestCredentialService();
        var current = new CredentialInfo { UserName = @"CONTOSO\opsuser" };
        var other = new CredentialInfo { UserName = @"CONTOSO\opsuser2" };
        service.Add(current);
        service.Add(other);
        service.SetSelectedCredential(current);

        using var vm = new CredentialViewModel(service, new TestLogService());
        vm.ListSelectedCredential = other;

        Assert.Same(current, service.SelectedCredential);

        vm.SetCurrentCredentialCommand.Execute(null);

        Assert.Same(other, service.SelectedCredential);
    }

    [Fact]
    public void CredentialViewModel_ListSelectionFallsBackWhenSelectedCredentialRemoved()
    {
        var service = new TestCredentialService();
        var first = new CredentialInfo { UserName = @"CONTOSO\opsuser" };
        var second = new CredentialInfo { UserName = @"CONTOSO\opsuser2" };
        service.Add(first);
        service.Add(second);
        service.SetSelectedCredential(first);

        using var vm = new CredentialViewModel(service, new TestLogService());
        vm.ListSelectedCredential = second;

        service.Remove(second);
        vm.EnsureListSelectionConsistent();

        Assert.Same(first, vm.ListSelectedCredential);
    }

    [Fact]
    public void CredentialViewModel_ListSelectionClearedAfterClearAll()
    {
        var service = new TestCredentialService();
        var only = new CredentialInfo { UserName = @"CONTOSO\opsuser" };
        service.Add(only);

        using var vm = new CredentialViewModel(service, new TestLogService());
        vm.ListSelectedCredential = only;

        service.ClearAll();
        vm.EnsureListSelectionConsistent();

        Assert.Null(vm.ListSelectedCredential);
    }

    private static RemoteCapabilityInfo Success(string name) => new()
    {
        Name = name,
        Success = true,
        Detail = "ok",
    };

    private static RemoteCapabilityInfo Failure(string name, string detail) => new()
    {
        Name = name,
        Success = false,
        Detail = detail,
    };

    private static string CreateTempCredentialPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteOpsTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "credentials.dat");
    }

    private static async Task DeleteTempCredentialPathAsync(string path)
    {
        await Task.Delay(650);
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void CredentialService_EncryptPassword_UsesProtectionEntropy()
    {
        const string password = "P@ssw0rd!";
        var encrypted = CredentialService.EncryptPassword(password);
        var encryptedBytes = Convert.FromBase64String(encrypted);
        byte[] decryptedBytes;
        try
        {
            decryptedBytes = System.Security.Cryptography.ProtectedData.Unprotect(
                encryptedBytes, CredentialService.ProtectionEntropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encryptedBytes);
        }

        try
        {
            Assert.Equal(password, System.Text.Encoding.UTF8.GetString(decryptedBytes));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decryptedBytes);
        }
    }

    [Fact]
    public async Task CredentialService_TryDecryptPassword_MigratesLegacyEntropyOnUse()
    {
        const string password = @"P@ss\ word""quote!";
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(password);
        string legacyEncrypted;
        try
        {
            legacyEncrypted = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                plainBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainBytes);
        }

        var path = CreateTempCredentialPath();
        try
        {
            var credential = new CredentialInfo
            {
                UserName = @"CONTOSO\opsuser",
                EncryptedPassword = legacyEncrypted,
            };
            var service = new CredentialService(new TestLogService(), path);

            Assert.True(service.TryDecryptPassword(credential, out var decrypted));

            Assert.Equal(password, decrypted);
            Assert.NotEqual(legacyEncrypted, credential.EncryptedPassword);

            Assert.True(service.TryDecryptPassword(credential, out var migratedDecrypted));
            Assert.Equal(password, migratedDecrypted);
        }
        finally
        {
            await DeleteTempCredentialPathAsync(path);
        }
    }}
