using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Telematic.AssettoServer.Plugin;

[ApiController]
[Route("/telematic")]
[AllowAnonymous]
public sealed class TelematicController : ControllerBase
{
    private readonly TelematicConfiguration _configuration;
    private readonly TelematicGatewayClient _gateway;
    private readonly AssettoEventBridge _events;

    public TelematicController(TelematicConfiguration configuration, TelematicGatewayClient gateway, AssettoEventBridge events)
    { _configuration = configuration; _gateway = gateway; _events = events; }

    [HttpGet("status")]
    public IActionResult Status()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!NetworkExposure.DiagnosticsAuthorized(HttpContext, _configuration.DiagnosticsToken, _configuration.PluginToken)) return Unauthorized();
        var gateway = _gateway.Diagnostics();
        var events = _events.Diagnostics();
        return Ok(new
        {
            plugin = new { enabled = _configuration.Enabled, instanceId = _configuration.InstanceId, telemetryEnabled = _configuration.Telemetry.Enabled },
            gateway,
            events = new { events.CompletedLaps, events.LastLapAt },
            checkedAt = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("")]
    [Produces("text/html")]
    public IActionResult Page()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Frame-Options"] = "DENY";
        if (!NetworkExposure.LocalRequest(HttpContext)) return StatusCode(403);
        if (_configuration.DiagnosticsToken.Length < 32 || _configuration.DiagnosticsToken == _configuration.PluginToken) return Unauthorized();
        // A local static login shell contains no runtime data or credentials.
        // Every status/polling response still requires the separate header.
        return Content(Html, "text/html");
    }

    private const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Telematic diagnostics</title>
<style>body{font:15px system-ui;background:#202526;color:#eee;max-width:760px;margin:40px auto;padding:0 20px}h1{font-size:25px}p{color:#a9c7d8}.card{border:1px solid #526269;border-radius:10px;padding:18px;margin:16px 0;background:#293032}.row{display:flex;justify-content:space-between;border-bottom:1px solid #3d484b;padding:9px 0}.row:last-child{border:0}.ok{color:#77dc9b}.warn{color:#ffc857}small{color:#9cb0b8}</style></head>
<body><h1>Telematic plugin diagnostics</h1><p>AssettoServer plugin-side view. A connected transport does not prove durable acceptance or cloud delivery. Never displays the plugin token.</p>
<div class="card"><h2>Connection</h2><div id="connection">Loading…</div></div><div class="card"><h2>Durable delivery</h2><div id="durable">Loading…</div></div><div class="card"><h2>Gameplay events</h2><div id="events">Loading…</div></div><small id="checked"></small>
<script>
const diagnosticToken=prompt('Enter the separate local diagnostics credential (held only in memory):')??'';
const esc=v=>String(v??'—').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const time=v=>v?new Date(v).toLocaleString():'—';
async function refresh(){try{const s=await fetch('/telematic/status',{cache:'no-store',headers:{'X-Telematic-Diagnostics-Token':diagnosticToken}}).then(r=>r.json());const g=s.gateway,e=s.events;
document.querySelector('#connection').innerHTML='<div class="row"><b>Status</b><span class="'+(g.connected?'ok':'warn')+'">'+(g.connected?'Connected':'Not connected')+'</span></div><div class="row"><b>Instance</b><span>'+esc(s.plugin.instanceId)+'</span></div><div class="row"><b>Last gateway message</b><span>'+esc(g.lastMessageType)+' · '+time(g.lastMessageAt)+'</span></div><div class="row"><b>Queued / awaiting ACK</b><span>'+g.queuedGameplayMessages+' / '+g.awaitingAcknowledgement+'</span></div><div class="row"><b>Last error</b><span class="warn">'+esc(g.lastError)+'</span></div>';
document.querySelector('#events').innerHTML='<div class="row"><b>Lap callbacks seen</b><span>'+e.completedLaps+'</span></div><div class="row"><b>Last lap</b><span>'+time(e.lastLapAt)+'</span></div><div class="row"><b>Session</b><span>[redacted]</span></div>';
const q=g.spool;document.querySelector('#durable').innerHTML='<div class="row"><b>Plugin spool</b><span class="'+(q?.storageHealthy?'ok':'warn')+'">'+(q?.storageHealthy?'Healthy':esc(q?.errorCode??'Unavailable / degraded'))+'</span></div><div class="row"><b>Pending / rejected / quarantined</b><span>'+esc(q?.pending)+' / '+esc(q?.rejected)+' / '+esc(q?.quarantined)+'</span></div><div class="row"><b>Spool bytes / capacity</b><span>'+esc(q?.bytesUsed)+' / '+esc(q?.capacityBytes)+'</span></div><div class="row"><b>Oldest pending</b><span>'+time(q?.oldestPendingAt)+'</span></div><div class="row"><b>Accepted / retrying / capture failures</b><span>'+g.accepted+' / '+g.retrying+' / '+g.captureFailures+'</span></div><div class="row"><b>Last durable acceptance</b><span>'+time(g.lastDurableAckAt)+'</span></div><div class="row"><b>Reconnects</b><span>'+g.reconnectCount+'</span></div><div class="row"><b>Cloud receipt</b><span>Unknown — inspect Supervisor/cloud</span></div>';
document.querySelector('#checked').textContent='Checked '+time(s.checkedAt);}catch(x){document.querySelector('#connection').innerHTML='<span class="warn">Status endpoint unavailable: '+esc(x)+'</span>';}}refresh();setInterval(refresh,2000);
</script><script>setInterval(async()=>{try{const s=await fetch('/telematic/status',{cache:'no-store',headers:{'X-Telematic-Diagnostics-Token':diagnosticToken}}).then(r=>r.json()),g=s.gateway;let box=document.querySelector('#correlation');if(!box){box=document.createElement('div');box.id='correlation';document.querySelector('#connection').parentElement.appendChild(box)}const ack=g.lastAcknowledgedAccepted===null||g.lastAcknowledgedAccepted===undefined?'—':(g.lastAcknowledgedAccepted?'accepted':'rejected');box.innerHTML='<div class="row"><b>Last durable sent</b><span>'+esc(g.lastDurableMessageType)+' · '+esc(g.lastDurableMessageId)+' · '+time(g.lastDurableSentAt)+'</span></div><div class="row"><b>Last ACK</b><span>'+esc(g.lastAcknowledgedMessageId)+' · '+ack+' · '+time(g.lastAcknowledgedAt)+'</span></div>'}catch{}} ,2000);</script></body></html>
""";
}
