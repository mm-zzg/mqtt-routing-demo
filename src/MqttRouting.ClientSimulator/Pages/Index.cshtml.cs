using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MqttRouting.ClientSimulator.Services;

namespace MqttRouting.ClientSimulator.Pages;

public class IndexModel : PageModel
{
    private readonly ClientSimulatorManager _manager;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ClientSimulatorManager manager, ILogger<IndexModel> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    [BindProperty]
    public CertificateInput NewCertificate { get; set; } = new();

    [BindProperty]
    public ClientInput NewClient { get; set; } = new();

    public List<ClientCertificateRecord> Certificates { get; private set; } = [];

    public IReadOnlyList<ClientRuntimeSnapshot> Clients { get; private set; } = [];

    public async Task OnGetAsync() => await ReloadAsync();

    public async Task<IActionResult> OnPostAddCertificateAsync()
    {
        if (!ModelState.IsValid)
        {
            await ReloadAsync();
            return Page();
        }

        try
        {
            await _manager.AddCertificateAsync(NewCertificate);
            TempData["StatusMessage"] = "Certificate added.";
            return RedirectToPage();
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Invalid PFX base64 input.");
            ModelState.AddModelError("NewCertificate.PfxBase64", "The PFX input must be valid base64.");
            await ReloadAsync();
            return Page();
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Invalid certificate payload.");
            ModelState.AddModelError("NewCertificate.Password", "The PFX or password is invalid.");
            await ReloadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAddClientAsync()
    {
        if (!ModelState.IsValid)
        {
            await ReloadAsync();
            return Page();
        }

        try
        {
            await _manager.AddClientAsync(NewClient);
            TempData["StatusMessage"] = "Client profile created.";
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Unable to add client.");
            ModelState.AddModelError("NewClient.CertificateId", ex.Message);
            await ReloadAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostStartClientAsync(string clientId)
    {
        await _manager.StartClientAsync(clientId, HttpContext.RequestAborted);
        TempData["StatusMessage"] = "Client started.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStopClientAsync(string clientId)
    {
        await _manager.StopClientAsync(clientId);
        TempData["StatusMessage"] = "Client stopped.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveClientAsync(string clientId)
    {
        await _manager.RemoveClientAsync(clientId);
        TempData["StatusMessage"] = "Client removed.";
        return RedirectToPage();
    }

    private async Task ReloadAsync()
    {
        Certificates = await _manager.GetCertificatesAsync();
        Clients = _manager.GetClients();
    }
}
