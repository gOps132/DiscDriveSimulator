using System.Text.Json;
using Microsoft.JSInterop;
using DiscDriveSimulator.Models;

namespace DiscDriveSimulator.Services;

public class StorageService
{
    private readonly IJSRuntime _js;
    private const string StorageKey = "zzz_disc_sim_state_v1";

    public StorageService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task SaveStateAsync(SimulationState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state);
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // Silently ignore storage errors (e.g. quota or SSR)
        }
    }

    public async Task<SimulationState?> LoadStateAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                return JsonSerializer.Deserialize<SimulationState>(json);
            }
        }
        catch
        {
            // Return null if deserialization or storage fails
        }
        return null;
    }

    public async Task ClearStateAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch
        {
        }
    }
}
