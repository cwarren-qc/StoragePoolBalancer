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

if (!window.Balancer.getDetailsPref) {
    window.Balancer.getDetailsPref = function (id, defaultVal = null) {
        try {
            const val = localStorage.getItem('user_details_' + id);
            if (val === 'open') return true;
            if (val === 'closed') return false;
        } catch (e) {}
        return defaultVal;
    };
}

if (!window.Balancer.saveDetailsPref) {
    window.Balancer.saveDetailsPref = function (id, isOpen) {
        try {
            localStorage.setItem('user_details_' + id, isOpen ? 'open' : 'closed');
        } catch (e) {}
    };
}

// Scan Status Metadata
const volumeScanStatusReady = 'Ready';
const volumeScanStatusQueued = 'Queued';
const volumeScanStatusScanning = 'Scanning';
const volumeScanStatusComplete = 'Complete';
const volumeScanStatusCancelled = 'Cancelled';
const volumeScanStatusFailed = 'Failed';

const volumeScanStatusConfigs = [
    {
        name: volumeScanStatusReady,
        displayName: 'Ready',
        render(vol) {
            return '<span class="state-text"><span class="status-dot dot-wait" style="background:transparent; border:1px solid var(--muted);"></span>Ready</span>';
        }
    },
    {
        name: volumeScanStatusQueued,
        displayName: 'Queued',
        render(vol, displayVolumes) {
            const scanningVol = (displayVolumes || []).find(v => v.diskName === vol.diskName && v.status === volumeScanStatusScanning);
            const waitText = scanningVol ? `Waiting (${scanningVol.alias})` : 'Waiting';
            return `<span class="state-text"><span class="status-dot dot-wait"></span>${window.Balancer.escapeHtml(waitText)}</span>`;
        }
    },
    {
        name: volumeScanStatusScanning,
        displayName: 'Scanning',
        render(vol) {
            return '<span class="state-text"><span class="status-dot dot-scan"></span>Scanning</span>';
        }
    },
    {
        name: volumeScanStatusComplete,
        displayName: 'Complete',
        render(vol) {
            const hasIssues = vol.issues && vol.issues.length > 0;
            if (hasIssues) {
                return `<span class="state-pill state-failed">Issues (${vol.issues.length})</span>`;
            }
            return '<span class="state-pill state-complete">Complete</span>';
        }
    },
    {
        name: volumeScanStatusCancelled,
        displayName: 'Cancelled',
        render(vol) {
            return '<span class="state-pill" style="background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6;">Cancelled</span>';
        }
    },
    {
        name: volumeScanStatusFailed,
        displayName: 'Failed',
        render(vol) {
            return '<span class="state-pill state-failed">Failed</span>';
        }
    }
];

// Scanner Subsystem
window.Balancer.scan = {
    statusConfigs: volumeScanStatusConfigs,
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

    formatDuration(timeTaken, startedAt, completedAt) {
        if (typeof timeTaken === 'string') {
            const parts = timeTaken.split('.')[0].split(':');
            if (parts.length >= 3) {
                const hh = parts[0].padStart(2, '0');
                const mm = parts[1].padStart(2, '0');
                const ss = parts[2].padStart(2, '0');
                return `${hh}:${mm}:${ss}`;
            }
        }
        if (startedAt) {
            const start = new Date(startedAt).getTime();
            const end = completedAt ? new Date(completedAt).getTime() : Date.now();
            const totalSec = Math.max(0, Math.floor((end - start) / 1000));
            const hours = Math.floor(totalSec / 3600);
            const minutes = Math.floor((totalSec % 3600) / 60);
            const seconds = totalSec % 60;
            return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
        }
        return '00:00:00';
    },

    renderPills(statusOrVolumes, pillsContainerEl) {
        const el = typeof pillsContainerEl === 'string' ? document.getElementById(pillsContainerEl) : pillsContainerEl;
        if (!el) return;

        const isArr = Array.isArray(statusOrVolumes);
        const volumes = isArr ? statusOrVolumes : (statusOrVolumes?.volumes || []);
        const status = isArr ? null : statusOrVolumes;

        const volumesCount = volumes.length;
        const totalFiles = volumes.reduce((sum, v) => sum + (Number(v.filesScanned) || 0), 0);
        const totalFolders = volumes.reduce((sum, v) => sum + (Number(v.foldersScanned) || 0), 0);
        const totalBytes = volumes.reduce((sum, v) => sum + (Number(v.bytesScanned) || 0), 0);

        const timeTakenStr = this.formatDuration(status?.timeTaken, status?.startedAt, status?.completedAt);

        const pillStyle = 'font-size:11px; padding:2px 8px; background:#eaf8ef; color:#1b6e32; border:1px solid #c2e9cb;';

        el.innerHTML = `
            <span class="state-pill" style="${pillStyle}" title="${volumesCount} volume(s) scanned">Volumes: ${volumesCount}</span>
            <span class="state-pill" style="${pillStyle}" title="${totalFiles.toLocaleString()} files cataloged">Files: ${totalFiles.toLocaleString()}</span>
            <span class="state-pill" style="${pillStyle}" title="${totalFolders.toLocaleString()} folders cataloged">Folders: ${totalFolders.toLocaleString()}</span>
            <span class="state-pill" style="${pillStyle}" title="${window.Balancer.formatBytes(totalBytes)} scanned">Data: ${window.Balancer.formatBytes(totalBytes)}</span>
            <span class="state-pill" style="${pillStyle}" title="Scan duration">Duration: ${timeTakenStr}</span>
        `;
    },

    renderTable(statusOrVolumes, tbodyEl, fallbackConfiguredVolumes, options = {}) {
        if (!tbodyEl) return;
        const escapeHtml = window.Balancer.escapeHtml;
        const formatBytes = window.Balancer.formatBytes;

        const isArr = Array.isArray(statusOrVolumes);
        let displayVolumes = isArr ? statusOrVolumes : (statusOrVolumes?.volumes || []);
        const status = isArr ? null : statusOrVolumes;

        // Fallback for fresh boot before scan started
        if (displayVolumes.length === 0 && fallbackConfiguredVolumes && fallbackConfiguredVolumes.length > 0) {
            displayVolumes = fallbackConfiguredVolumes.map(v => ({
                alias: v.alias,
                diskName: v.disk,
                status: volumeScanStatusReady,
                currentPath: '-',
                filesScanned: 0,
                foldersScanned: 0,
                bytesScanned: 0,
                issues: [],
                error: null
            }));
        }

        const pillsEl = options.pillsEl || document.getElementById('scan-summary-pills');
        if (pillsEl) {
            const pillsInput = (isArr && displayVolumes.length > 0) ? displayVolumes : { ...(status || {}), volumes: displayVolumes };
            this.renderPills(pillsInput, pillsEl);
        }

        if (displayVolumes.length === 0) {
            tbodyEl.innerHTML = '<tr><td colspan="6" style="text-align:center; color:var(--muted); padding:20px;">No volumes or scan activity.</td></tr>';
            return;
        }

        tbodyEl.innerHTML = displayVolumes.map(vol => {
            let stateHtml = '';
            const hasIssues = vol.issues && vol.issues.length > 0;
            const hasError = !!vol.error;

            if (hasError || vol.status === volumeScanStatusFailed) {
                stateHtml = '<span class="state-pill state-failed">Failed</span>';
            } else {
                const config = volumeScanStatusConfigs.find(c => c.name === vol.status);
                if (config) {
                    stateHtml = config.render(vol, displayVolumes);
                } else {
                    stateHtml = `<span class="state-text"><span class="status-dot dot-wait"></span>${escapeHtml(vol.status || 'Unknown')}</span>`;
                }
            }

            const isPending = vol.status === volumeScanStatusQueued || vol.status === volumeScanStatusReady;
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
    },

    renderSection(containerEl, options = {}) {
        const el = typeof containerEl === 'string' ? document.getElementById(containerEl) : containerEl;
        if (!el) return;

        const openAttr = options.open ? 'open' : '';
        const containerClass = options.containerClass ? ` class="${options.containerClass}"` : '';
        const titleText = options.titleText || '🔍 State Scan';
        const subtitleText = options.subtitleText || '';
        const showNotice = options.showNotice ?? true;

        let controlsHtml = options.controlsHtml || '';
        if (!controlsHtml && options.showControls) {
            controlsHtml = `
                <input type="text" id="scan-name" placeholder="Snapshot Name (Optional)" title="Optional custom name for this scan snapshot. Defaults to date and time if empty" style="width:220px; padding:6px 10px; border:1px solid var(--line); border-radius:5px; background:var(--surface); font-size:13px; font-weight:normal;">
                <button class="btn danger" id="btn-scan-cancel" onclick="event.stopPropagation(); ${options.onCancel || 'cancelScan()'}" style="display:none;" title="Cancel the currently running pool scan">Cancel Scan</button>
                <button class="btn primary" id="btn-scan-start" onclick="event.stopPropagation(); document.getElementById('details-scan').open = true; ${options.onStart || 'startScan()'};" title="Start scanning all configured volumes to catalog files, folders, and sizes">Start Pool Scan</button>
            `;
        }

        const placeholderClassAttr = options.placeholderClass ? ` class="${options.placeholderClass}"` : '';
        const placeholderStyleAttr = options.placeholderStyle ? ` style="${options.placeholderStyle}"` : (options.placeholderClass ? '' : ' style="text-align:center; color:var(--muted); padding:20px;"');
        const placeholderText = options.placeholderText || 'No scan running.';

        const volPref = window.Balancer.getDetailsPref ? window.Balancer.getDetailsPref('scan-details-volumes') : null;
        const volOpen = volPref !== null ? volPref : (options.volumesOpen ?? true);

        el.innerHTML = `
            <details id="details-scan"${containerClass} ${openAttr}>
                <summary style="display:flex; justify-content:space-between; align-items:center; gap:12px; flex-wrap:wrap;">
                    <div class="summary-title">
                        <span>${titleText}</span>
                        <span class="summary-subtitle" id="scan-subtitle">${subtitleText}</span>
                    </div>
                    ${controlsHtml ? `<div style="display:flex; align-items:center; gap:8px;" onclick="event.stopPropagation();">${controlsHtml}</div>` : ''}
                </summary>
                <div class="content">
                    <details class="sub-details" id="scan-details-volumes"${volOpen ? ' open' : ''}>
                        <summary style="cursor:pointer; display:flex; justify-content:space-between; align-items:center;">
                            <span style="font-weight:700;" title="Volume storage scan status and cataloged metrics">Volumes</span>
                            <div id="scan-summary-pills" style="display:flex; align-items:center; gap:8px;"></div>
                        </summary>
                        <div class="sub-content" style="padding:16px 12px; overflow-x:auto;">
                            <table class="scan-table">
                                <thead>
                                    <tr>
                                        <th style="width: 70px;" title="Volume alias being scanned">Volume</th>
                                        <th style="width: 120px;" title="Current scan status: Scanning, Waiting, Queued, Ready, or Complete">State</th>
                                        <th style="width: 80px; text-align:right;" title="Total number of files cataloged on this volume">Files</th>
                                        <th style="width: 80px; text-align:right;" title="Total number of directories cataloged on this volume">Folders</th>
                                        <th style="width: 100px; text-align:right;" title="Total logical file size scanned on this volume">Logical Data</th>
                                        <th style="width: auto; padding-left: 16px;" title="Folder path currently being scanned on disk">Current Path</th>
                                    </tr>
                                </thead>
                                <tbody id="scan-tbody">
                                    <tr><td colspan="6"${placeholderClassAttr}${placeholderStyleAttr}>${placeholderText}</td></tr>
                                </tbody>
                            </table>
                        </div>
                    </details>
                    ${showNotice ? '<div id="scan-notice" class="notice"></div>' : ''}
                </div>
            </details>
        `;
    }
};

window.Balancer.scanner = window.Balancer.scan;
