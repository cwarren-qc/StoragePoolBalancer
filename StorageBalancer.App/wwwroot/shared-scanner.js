/**
 * Storage Pool Balancer - Shared Scanner Module
 */

window.Balancer = window.Balancer || {};

// Shared Utilities (defined if not already present)
if (!window.Balancer.escapeHtml) {
    window.Balancer.escapeHtml = function (str) {
        return String(str ?? '').replace(/[&<>"']/g, c => ({
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#39;'
        }[c]));
    };
}

if (!window.Balancer.formatBytes) {
    window.Balancer.formatBytes = function (bytes) {
        if (!bytes || bytes <= 0) return '0 B';
        const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
        const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
        return `${(bytes / (1024 ** i)).toFixed(i > 1 ? 1 : 0)} ${units[i]}`;
    };
}

if (!window.Balancer.getVolumeColorMap) {
    window.Balancer.getVolumeColorMap = function (volumes) {
        return new Map((volumes || []).map((v, index) => {
            const alias = typeof v === 'string' ? v : v.alias;
            return [alias, `hsl(${Math.round((index * 137.508 + 145) % 360)} 58% 42%)`];
        }));
    };
}

// Scanner Subsystem
window.Balancer.scan = {
    async fetchStatus() {
        const res = await fetch('/api/scan/status');
        if (!res.ok) throw new Error(`Failed to fetch scan status (${res.status})`);
        return await res.json();
    },

    async startScan(snapshotName) {
        const payload = snapshotName ? { snapshotName: snapshotName.trim() } : {};
        const res = await fetch('/api/scan', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok && res.status !== 202) {
            throw new Error(data.Error || data.error || 'Failed to start scan');
        }
        return data;
    },

    async cancelScan() {
        const res = await fetch('/api/scan/cancel', { method: 'POST' });
        const data = await res.json().catch(() => ({}));
        if (!res.ok && res.status !== 202) {
            throw new Error(data.Error || data.error || 'Failed to cancel scan');
        }
        return data;
    },

    renderTable(statusVolumes, tbodyEl, fallbackConfiguredVolumes) {
        if (!tbodyEl) return;
        const escapeHtml = window.Balancer.escapeHtml;
        const formatBytes = window.Balancer.formatBytes;

        let displayVolumes = statusVolumes || [];

        // Fallback for fresh boot before scan started
        if (displayVolumes.length === 0 && fallbackConfiguredVolumes && fallbackConfiguredVolumes.length > 0) {
            displayVolumes = fallbackConfiguredVolumes.map(v => ({
                alias: v.alias,
                diskName: v.disk,
                status: 'Ready',
                currentPath: '-',
                filesScanned: 0,
                foldersScanned: 0,
                bytesScanned: 0,
                issues: [],
                error: null
            }));
        }

        if (displayVolumes.length === 0) {
            tbodyEl.innerHTML = '<tr><td colspan="6" style="text-align:center; color:var(--muted); padding:20px;">No volumes or scan activity.</td></tr>';
            return;
        }

        tbodyEl.innerHTML = displayVolumes.map(vol => {
            let stateHtml = '';
            const hasIssues = vol.issues && vol.issues.length > 0;
            const hasError = !!vol.error;

            if (hasError || vol.status === 'Failed') {
                stateHtml = '<span class="state-pill state-failed">Failed</span>';
            } else if (hasIssues && vol.status === 'Complete') {
                stateHtml = `<span class="state-pill state-failed">Issues (${vol.issues.length})</span>`;
            } else if (vol.status === 'Complete') {
                stateHtml = '<span class="state-pill state-complete">Complete</span>';
            } else if (vol.status === 'Scanning') {
                stateHtml = '<span class="state-text"><span class="status-dot dot-scan"></span>Scanning</span>';
            } else if (vol.status === 'Queued' && vol.diskName) {
                const scanningVol = displayVolumes.find(v => v.diskName === vol.diskName && v.status === 'Scanning');
                const waitText = scanningVol ? `Waiting (${scanningVol.alias})` : 'Waiting';
                stateHtml = `<span class="state-text"><span class="status-dot dot-wait"></span>${escapeHtml(waitText)}</span>`;
            } else if (vol.status === 'Ready') {
                stateHtml = '<span class="state-text"><span class="status-dot dot-wait" style="background:transparent; border:1px solid var(--muted);"></span>Ready</span>';
            } else {
                stateHtml = `<span class="state-text"><span class="status-dot dot-wait"></span>${escapeHtml(vol.status)}</span>`;
            }

            const isPending = vol.status === 'Queued' || vol.status === 'Ready';
            const f = !isPending ? Number(vol.filesScanned || 0).toLocaleString() : '-';
            const d = !isPending ? Number(vol.foldersScanned || 0).toLocaleString() : '-';
            const b = !isPending ? formatBytes(vol.bytesScanned || 0) : '-';

            let rawPath = vol.currentPath || '-';
            if (rawPath === '') rawPath = '-';

            let issueHtml = '';
            if (hasError) {
                issueHtml = `<div style="color:var(--orange); font-size:11px; margin-top:4px; font-family:sans-serif; white-space:normal;">${escapeHtml(vol.error)}</div>`;
            } else if (hasIssues && vol.status === 'Complete') {
                issueHtml = `<div style="color:var(--orange); font-size:11px; margin-top:4px; font-family:sans-serif; white-space:normal;">${escapeHtml(vol.issues[0].message)}</div>`;
            }

            return `
                <tr>
                    <td><strong>${escapeHtml(vol.alias)}</strong></td>
                    <td>${stateHtml}</td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums;">${f}</td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums;">${d}</td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums;">${b}</td>
                    <td style="min-width:0; width:100%;">
                        <div style="white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font-family:monospace; font-size:11px; color:var(--muted);" title="${escapeHtml(rawPath)}">
                            ${escapeHtml(rawPath)}
                        </div>
                        ${issueHtml}
                    </td>
                </tr>
            `;
        }).join('');
    }
};
