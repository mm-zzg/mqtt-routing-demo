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

    public IReadOnlyList<ClientCertificateRecord> Certificates { get; private set; } = [];

    public IReadOnlyList<ClientRuntimeSnapshot> Clients { get; private set; } = [];

    public void OnGet() => Reload();

    public IActionResult OnPostAddCertificate()
    {
        if (!ModelState.IsValid)
        {
            Reload();
            return Page();
        }

        try
        {
            _manager.AddCertificate(NewCertificate);
            TempData["StatusMessage"] = "Certificate added.";
            return RedirectToPage();
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Invalid PFX base64 input.");
            ModelState.AddModelError("NewCertificate.PfxBase64", "The PFX input must be valid base64.");
            Reload();
            return Page();
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Invalid certificate payload.");
            ModelState.AddModelError("NewCertificate.Password", "The PFX or password is invalid.");
            Reload();
            return Page();
        }
    }

    public IActionResult OnPostAddClient()
    {
        if (!ModelState.IsValid)
        {
            Reload();
            return Page();
        }

        try
        {
            _manager.AddClient(NewClient);
            TempData["StatusMessage"] = "Client profile created.";
            return RedirectToPage();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Unable to add client.");
            ModelState.AddModelError("NewClient.CertificateId", ex.Message);
            Reload();
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

    private void Reload()
    {
        Certificates = _manager.GetCertificates();
        Clients = _manager.GetClients();
    }
}
