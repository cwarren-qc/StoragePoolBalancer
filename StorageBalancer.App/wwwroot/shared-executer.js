/**
 * Storage Pool Balancer - Shared Executer Module
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

// Proportional Dual Bar Renderer with Shared Scale Cut
function renderVolumeDualBars(startSegments, startUsed, endSegments, endUsed, capacity, volumeColors) {
    const escapeHtml = window.Balancer.escapeHtml;
    const formatBytes = window.Balancer.formatBytes;

    if (capacity <= 0) {
        const emptyBar = `<div class="provenance-bar" style="margin:0; height:13px; display:flex; background:#e4e8e1; border-radius:3px;"></div>`;
        return { startBarHtml: emptyBar, endBarHtml: emptyBar };
    }

    const maxUsed = Math.max(startUsed, endUsed);
    const minFreeBytes = Math.max(0, capacity - maxUsed);
    const minFreePct = capacity > 0 ? (minFreeBytes / capacity) * 100 : 0;

    // Both bars share the exact same scale so that fixed/stayed data aligns vertically with 100% precision!
    const isCut = minFreePct > 30;
    const visualMaxFreePct = isCut ? 30 : minFreePct;
    const visualMaxUsedPct = 100 - visualMaxFreePct;

    // Scale: percentage of bar width per byte of data
    const scale = maxUsed > 0 ? (visualMaxUsedPct / maxUsed) : 0;

    function buildBar(segments, usedBytes) {
        if (usedBytes <= 0) {
            return `<div class="provenance-bar" style="margin:0; height:13px; display:flex; background:#e4e8e1; border-radius:3px; overflow:hidden;">
                <div class="free-segment" style="width:100%;" title="${formatBytes(capacity)} free (100% of capacity)"></div>
            </div>`;
        }

        const segmentElements = segments.filter(s => s.size > 0).map(item => {
            const color = volumeColors.get(item.alias) || '#89958f';
            const pctOfBar = item.size * scale;
            const isOtherClass = item.isOther ? ' is-other' : '';
            return `<span class="provenance-segment${isOtherClass}" style="width:${pctOfBar}%; background-color:${color};" title="${escapeHtml(item.tooltip)}"></span>`;
        }).join('');

        const freeBytes = Math.max(0, capacity - usedBytes);
        const actualFreePct = (freeBytes / capacity) * 100;
        const usedBarPct = usedBytes * scale;
        const visualFreePct = Math.max(0, 100 - usedBarPct);

        let freeElement = '';
        if (visualFreePct > 0) {
            const barIsCut = actualFreePct > 30;
            const cutClass = barIsCut ? 'is-cut' : '';
            const cutTitle = barIsCut
                ? `${formatBytes(freeBytes)} free (${actualFreePct.toFixed(1)}% of capacity - cut representation)`
                : `${formatBytes(freeBytes)} free (${actualFreePct.toFixed(1)}% of capacity)`;

            const cutBreak = barIsCut ? '<span class="break-indicator" title="Scale cut break: Free space exceeds 30%"></span>' : '';
            freeElement = `<div class="free-segment ${cutClass}" style="width:${visualFreePct}%;" title="${escapeHtml(cutTitle)}">${cutBreak}</div>`;
        }

        return `<div class="provenance-bar" style="margin:0; height:13px; display:flex; background:#d4dbd1; border-radius:3px; overflow:hidden;">
            ${segmentElements}
            ${freeElement}
        </div>`;
    }

    return {
        startBarHtml: buildBar(startSegments, startUsed),
        endBarHtml: buildBar(endSegments, endUsed)
    };
}

// Execution Phase & Activity Status Metadata
const executionPhaseIdle = 'Idle';
const executionPhasePreparing = 'Preparing';
const executionPhaseTransferring = 'Transferring';
const executionPhaseCleaningFolders = 'CleaningFolders';
const executionPhaseCompleted = 'Completed';
const executionPhaseCancelled = 'Cancelled';
const executionPhaseFailed = 'Failed';

const volumeActivityIdle = 'Idle';
const volumeActivityReading = 'Reading';
const volumeActivityWriting = 'Writing';
const volumeActivityCleaningFolders = 'CleaningFolders';
const volumeActivityComplete = 'Complete';

function renderTransferActivity(active, action, dotColor) {
    const speedText = active?.throughputBps > 0 ? `(${window.Balancer.formatBytes(active.throughputBps)}/s)` : '';
    return `
        <div style="display:flex; align-items:center; gap:6px; overflow:hidden;" title="${window.Balancer.escapeHtml(active?.fileName || '')}">
            <span class="status-dot dot-scan" style="background:${dotColor}; flex-shrink:0;"></span>
            <div style="white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font-size:12px;">
                <strong>${window.Balancer.escapeHtml(action)}:</strong> "${window.Balancer.escapeHtml(active?.fileName || '')}" <span style="color:var(--muted); font-size:11px;">${speedText}</span>
            </div>
        </div>
    `;
}

const volumeActivityConfigs = [
    {
        name: volumeActivityWriting,
        displayName: 'Writing',
        render: (active, action = 'Writing') => renderTransferActivity(active, action, '#2085ec')
    },
    {
        name: volumeActivityReading,
        displayName: 'Reading',
        render: (active, action = 'Reading') => renderTransferActivity(active, action, 'var(--green)')
    },
    {
        name: volumeActivityCleaningFolders,
        displayName: 'Cleaning Folders',
        render: () => `
            <div style="display:flex; align-items:center; gap:6px;">
                <span class="status-dot dot-scan" style="background:var(--green); flex-shrink:0;"></span>
                <span style="font-size:12px; font-weight:600;">Cleaning Folders</span>
            </div>
        `
    },
    {
        name: volumeActivityComplete,
        displayName: 'Complete',
        render: () => '<span class="state-pill state-complete">Complete</span>'
    },
    {
        name: volumeActivityIdle,
        displayName: 'Idle',
        render: () => '<span class="state-text"><span class="status-dot dot-wait"></span>Idle</span>'
    }
];

// Executer Subsystem
window.Balancer.execution = {
    phases: {
        idle: executionPhaseIdle,
        preparing: executionPhasePreparing,
        transferring: executionPhaseTransferring,
        cleaningFolders: executionPhaseCleaningFolders,
        completed: executionPhaseCompleted,
        cancelled: executionPhaseCancelled,
        failed: executionPhaseFailed
    },
    activityStatuses: {
        idle: volumeActivityIdle,
        reading: volumeActivityReading,
        writing: volumeActivityWriting,
        cleaningFolders: volumeActivityCleaningFolders,
        complete: volumeActivityComplete
    },
    activityConfigs: volumeActivityConfigs,
    async start(options = {}) {
        const res = await fetch('/api/execution/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                snapshotName: options.snapshotName || null,
                isSimulation: options.isSimulation !== false,
                simulationDurationSeconds: (options.simulationDurationSeconds !== undefined && options.simulationDurationSeconds !== null) ? options.simulationDurationSeconds : 120,
                maxThreads: options.maxThreads || null,
                verifyCopies: options.verifyCopies !== false,
                maxFilesToCopy: options.maxFilesToCopy || null
            })
        });

        const data = await res.json();
        if (!res.ok) {
            throw new Error(data.error || 'Failed to start execution');
        }
        return data;
    },

    async fetchStatus() {
        const res = await fetch('/api/execution/status');
        if (!res.ok) {
            throw new Error('Failed to retrieve execution status');
        }
        return await res.json();
    },

    async cancel() {
        const res = await fetch('/api/execution/cancel', { method: 'POST' });
        if (!res.ok) {
            throw new Error('Failed to cancel execution');
        }
        return await res.json();
    },

    renderStatusSummary(status, elements = {}, options = {}) {
        if (!status) return;
        const formatBytes = window.Balancer.formatBytes;
        const isSimulation = Boolean(options.isSimulation);

        const {
            subtitle,
            pct: execPct,
            progressFill: execProgressFill,
            transferred: execTransferred,
            files: execFiles,
            speed: execSpeed,
            foldersCleaned: execFoldersCleaned,
            summaryBar,
            notice
        } = elements;

        const isCompleted = Boolean(status.completedAt || status.phase === 'Completed');

        if (summaryBar && (status.isRunning || isCompleted)) {
            summaryBar.style.display = 'flex';
        }

        if (status.isRunning) {
            const isPrep = status.phase === 'Preparing' || !status.volumes || status.volumes.length === 0;
            const isCleaning = status.phase === 'CleaningFolders' || status.phase === 'Cleaning Folders';

            if (isPrep) {
                if (subtitle) subtitle.innerHTML = '<span class="spinner" style="width:12px; height:12px; margin-right:6px;"></span> Preparing execution... (Computing placement plan)';
                if (execPct) execPct.textContent = 'Preparing...';
                if (execTransferred) execTransferred.textContent = 'Analyzing...';
                if (execFiles) execFiles.textContent = 'Analyzing...';
                if (execSpeed) execSpeed.textContent = '0 MB/s';
                if (execFoldersCleaned) execFoldersCleaned.textContent = '-';
            } else if (isCleaning) {
                if (subtitle) subtitle.innerHTML = '<span class="spinner" style="width:12px; height:12px; margin-right:6px;"></span> Processing safe empty folder cleanup across volumes...';
                if (execPct) execPct.textContent = '100%';
                if (execTransferred) execTransferred.textContent = `${formatBytes(status.transferredBytes)} / ${formatBytes(status.totalBytes)}`;
                if (execFiles) execFiles.textContent = `${(status.transferredFiles || 0).toLocaleString()} / ${(status.totalFiles || 0).toLocaleString()}`;
                if (execSpeed) execSpeed.textContent = '0 MB/s';
                if (execFoldersCleaned) execFoldersCleaned.textContent = 'Cleaning...';
            } else {
                if (subtitle) {
                    if (status.isCancellationRequested) {
                        subtitle.textContent = isSimulation ? 'Cancelling simulation...' : 'Cancelling execution...';
                    } else {
                        subtitle.textContent = isSimulation ? 'Simulation running...' : 'Executing file balancing...';
                    }
                }
                const pct = status.progressPercent || 0;
                if (execPct) execPct.textContent = `${pct.toFixed(0)}%`;
                if (execProgressFill) execProgressFill.style.width = `${pct}%`;
                if (execTransferred) execTransferred.textContent = `${formatBytes(status.transferredBytes)} / ${formatBytes(status.totalBytes)}`;
                if (execFiles) execFiles.textContent = `${(status.transferredFiles || 0).toLocaleString()} / ${(status.totalFiles || 0).toLocaleString()}`;
                if (execSpeed) execSpeed.textContent = status.throughputBps > 0 ? `${(status.throughputBps / (1024 * 1024)).toFixed(1)} MB/s` : '0 MB/s';
                if (execFoldersCleaned) execFoldersCleaned.textContent = '-';
            }
        } else if (isCompleted) {
            const pct = status.error ? (status.progressPercent || 0) : 100;
            if (execPct) execPct.textContent = `${pct.toFixed(0)}%`;
            if (execProgressFill) execProgressFill.style.width = `${pct}%`;
            if (execTransferred) execTransferred.textContent = `${formatBytes(status.transferredBytes)} / ${formatBytes(status.totalBytes)}`;
            if (execFiles) execFiles.textContent = `${(status.transferredFiles || 0).toLocaleString()} / ${(status.totalFiles || 0).toLocaleString()}`;
            if (execSpeed) execSpeed.textContent = '0 MB/s';
            if (execFoldersCleaned) {
                execFoldersCleaned.textContent = status.folderCleanup ? (status.folderCleanup.cleanedCount || 0).toLocaleString() : '-';
            }

            if (status.error) {
                if (subtitle) subtitle.textContent = isSimulation ? 'Simulation stopped with error' : 'Execution stopped with error';
                if (notice) {
                    notice.textContent = status.error;
                    notice.className = 'notice error';
                }
            } else {
                if (subtitle) {
                    subtitle.textContent = status.completedAt ? `Completed at ${new Date(status.completedAt).toLocaleTimeString()}` : 'Completed';
                }
                if (notice) {
                    const errCount = (status.transferErrors || []).length;
                    if (errCount > 0) {
                        notice.textContent = `${isSimulation ? 'Simulation' : 'Execution'} completed with ${errCount} skipped file(s) due to locks or errors. See below for details.`;
                        notice.className = 'notice info';
                    } else {
                        notice.textContent = isSimulation ? 'Simulation finished successfully.' : 'Execution finished successfully.';
                        notice.className = 'notice success';
                    }
                }
            }
        } else {
            if (subtitle) subtitle.textContent = '';
            if (execFoldersCleaned) execFoldersCleaned.textContent = '-';
        }
    },

    renderTable(status, tbodyEl, volumeColors) {
        if (!tbodyEl) return;
        const escapeHtml = window.Balancer.escapeHtml;
        const formatBytes = window.Balancer.formatBytes;

        const volumes = status?.volumes || [];
        if (!volumes || volumes.length === 0) {
            if (status?.isRunning) {
                tbodyEl.innerHTML = `
                    <tr>
                        <td colspan="6" style="text-align:center; padding:32px 16px; color:var(--deep); background:#eaf2ed; border-radius:6px;">
                            <div style="display:flex; align-items:center; justify-content:center; gap:10px; font-size:14px; font-weight:700;">
                                <span class="spinner" style="width:16px; height:16px;"></span>
                                <span>Preparing execution... Computing file placements and transfer queues...</span>
                            </div>
                            <div style="font-size:12px; color:var(--muted); margin-top:6px;">
                                Analyzing pool structure and balancing rules across volumes. Moves will begin in a few moments.
                            </div>
                        </td>
                    </tr>`;
                return;
            }
            tbodyEl.innerHTML = '<tr><td colspan="6" style="text-align:center; color:var(--muted); padding:20px;">No execution data available.</td></tr>';
            return;
        }

        const colors = volumeColors || window.Balancer.getVolumeColorMap(volumes);
        const activeTransfers = status.activeTransfers || [];

        // Build active transfers lookup by volume
        const transferByVol = new Map();
        activeTransfers.forEach(t => {
            if (t.sourceVolume && !transferByVol.has(t.sourceVolume)) {
                transferByVol.set(t.sourceVolume, { ...t, role: volumeActivityReading });
            }
            if (t.targetVolume && !transferByVol.has(t.targetVolume)) {
                transferByVol.set(t.targetVolume, { ...t, role: volumeActivityWriting });
            }
        });

        const sortedVols = [...volumes].sort((a, b) => a.alias.localeCompare(b.alias, undefined, { numeric: true }));

        tbodyEl.innerHTML = sortedVols.map(vol => {
            const color = colors.get(vol.alias) || '#89958f';
            const capacity = vol.capacity || 0;
            const otherItems = vol.otherItemsSizeOnDisk || 0;
            const stayedPool = Math.max(0, vol.stayedSize - otherItems);

            // 1. START BAR: Other + Stayed pool files + Remaining Outgoing to other volumes
            const startSegments = [];
            if (otherItems > 0) {
                startSegments.push({
                    alias: vol.alias,
                    size: otherItems,
                    isOther: true,
                    tooltip: `${formatBytes(otherItems)} Other items (outside PoolPart) on ${vol.alias}`
                });
            }
            if (stayedPool > 0) {
                startSegments.push({
                    alias: vol.alias,
                    size: stayedPool,
                    isOther: false,
                    tooltip: `${formatBytes(stayedPool)} pool files staying on ${vol.alias}`
                });
            }
            (vol.outgoingRemaining || []).forEach(seg => {
                if (seg.remainingBytes > 0) {
                    startSegments.push({
                        alias: seg.alias,
                        size: seg.remainingBytes,
                        isOther: false,
                        tooltip: `${formatBytes(seg.remainingBytes)} waiting to move to ${seg.alias} (of ${formatBytes(seg.totalBytes)})`
                    });
                }
            });
            const topTotalUsed = startSegments.reduce((sum, s) => sum + s.size, 0);

            // 2. END BAR: Other + Stayed pool files (expands as inbound land!) + Remaining Inbound
            const endSegments = [];
            if (otherItems > 0) {
                endSegments.push({
                    alias: vol.alias,
                    size: otherItems,
                    isOther: true,
                    tooltip: `${formatBytes(otherItems)} Other items (outside PoolPart) on ${vol.alias}`
                });
            }
            if (stayedPool > 0) {
                endSegments.push({
                    alias: vol.alias,
                    size: stayedPool,
                    isOther: false,
                    tooltip: `${formatBytes(stayedPool)} pool files on ${vol.alias}`
                });
            }
            (vol.incomingRemaining || []).forEach(seg => {
                if (seg.remainingBytes > 0) {
                    endSegments.push({
                        alias: seg.alias,
                        size: seg.remainingBytes,
                        isOther: false,
                        tooltip: `${formatBytes(seg.remainingBytes)} waiting to move in from ${seg.alias} (of ${formatBytes(seg.totalBytes)})`
                    });
                }
            });
            const bottomTotalUsed = endSegments.reduce((sum, s) => sum + s.size, 0);
            const { startBarHtml, endBarHtml } = renderVolumeDualBars(startSegments, topTotalUsed, endSegments, bottomTotalUsed, capacity, colors);

            // Activity status column
            let activityHtml = '';
            const active = transferByVol.get(vol.alias);
            const isAllDone = (vol.outgoingRemaining || []).every(s => s.remainingBytes <= 0) &&
                              (vol.incomingRemaining || []).every(s => s.remainingBytes <= 0);

            if (active) {
                const config = volumeActivityConfigs.find(c => c.name === active.role);
                activityHtml = config ? config.render(active) : `<span class="state-text">${escapeHtml(active.role)}</span>`;
            } else if (vol.status === volumeActivityCleaningFolders) {
                const config = volumeActivityConfigs.find(c => c.name === volumeActivityCleaningFolders);
                activityHtml = config ? config.render() : `<span class="state-text">${escapeHtml(vol.status)}</span>`;
            } else if (isAllDone && (vol.movedOutTotalFiles > 0 || vol.movedInTotalFiles > 0)) {
                const config = volumeActivityConfigs.find(c => c.name === volumeActivityComplete);
                activityHtml = config ? config.render() : `<span class="state-pill state-complete">Complete</span>`;
            } else if (vol.movedOutTotalFiles === 0 && vol.movedInTotalFiles === 0) {
                activityHtml = `<span class="state-text" style="color:var(--muted); font-size:12px;">Balanced</span>`;
            } else {
                const config = volumeActivityConfigs.find(c => c.name === volumeActivityIdle);
                activityHtml = config ? config.render() : `<span class="state-text"><span class="status-dot dot-wait"></span>Idle</span>`;
            }

            const remOutBytes = (vol.outgoingRemaining || []).reduce((sum, s) => sum + s.remainingBytes, 0);
            const remInBytes = (vol.incomingRemaining || []).reduce((sum, s) => sum + s.remainingBytes, 0);
            const remOutFiles = Math.max(0, (vol.movedOutTotalFiles || 0) - (vol.movedOutFiles || 0));
            const remInFiles = Math.max(0, (vol.movedInTotalFiles || 0) - (vol.movedInFiles || 0));

            const diskLabel = vol.diskName && vol.diskName !== vol.alias ? ` <span style="color:var(--muted); font-weight:normal; font-size:11px;">(${escapeHtml(vol.diskName)})</span>` : '';

            return `
                <tr>
                    <td>
                        <div style="display:flex; align-items:center; gap:8px;">
                            <span style="width:13px; height:13px; border-radius:50%; background:${color}; flex-shrink:0; box-shadow: inset 0 0 0 1px rgba(0,0,0,0.1);"></span>
                            <div>
                                <strong style="font-size:13px;">${escapeHtml(vol.alias)}</strong>${diskLabel}
                            </div>
                        </div>
                    </td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums; font-weight:600; color:var(--ink);">
                        ${formatBytes(capacity)}
                    </td>
                    <td style="padding-left:12px; padding-right:12px;">
                        <div class="dual-bar-container">
                            <div class="bar-row">
                                <span class="bar-stage-tag" title="Live volume state and remaining files moving out to other volumes">START (-OUT)</span>
                                ${startBarHtml}
                            </div>
                            <div class="bar-row">
                                <span class="bar-stage-tag" title="Target volume state and remaining files moving in from other volumes">END (+IN)</span>
                                ${endBarHtml}
                            </div>
                        </div>
                    </td>
                    <td style="max-width:260px; overflow:hidden;">
                        ${activityHtml}
                    </td>
                    <td style="text-align:right;">
                        <div class="dual-val-container">
                            <div class="val-row ${remOutBytes > 0 ? 'val-out' : 'val-dim'}" title="Remaining data to move out: ${formatBytes(remOutBytes)} (of ${formatBytes(vol.movedOutTotalBytes || 0)})">
                                ${remOutBytes > 0 ? `-${formatBytes(remOutBytes)}` : (vol.movedOutTotalBytes > 0 ? '0 B' : '-')}
                            </div>
                            <div class="val-row ${remInBytes > 0 ? 'val-in' : 'val-dim'}" title="Remaining data to move in: ${formatBytes(remInBytes)} (of ${formatBytes(vol.movedInTotalBytes || 0)})">
                                ${remInBytes > 0 ? `+${formatBytes(remInBytes)}` : (vol.movedInTotalBytes > 0 ? '0 B' : '-')}
                            </div>
                        </div>
                    </td>
                    <td style="text-align:right;">
                        <div class="dual-val-container">
                            <div class="val-row ${remOutFiles > 0 ? 'val-out' : 'val-dim'}" title="Remaining files to move out: ${remOutFiles} (of ${vol.movedOutTotalFiles || 0})">
                                ${remOutFiles > 0 ? `-${remOutFiles.toLocaleString()}` : (vol.movedOutTotalFiles > 0 ? '0' : '-')}
                            </div>
                            <div class="val-row ${remInFiles > 0 ? 'val-in' : 'val-dim'}" title="Remaining files to move in: ${remInFiles} (of ${vol.movedInTotalFiles || 0})">
                                ${remInFiles > 0 ? `+${remInFiles.toLocaleString()}` : (vol.movedInTotalFiles > 0 ? '0' : '-')}
                            </div>
                        </div>
                    </td>
                </tr>
            `;
        }).join('');

        // Populate Transfer Errors table if present in status
        const errorsDetails = document.getElementById('details-exec-errors');
        const errorsTbody = document.getElementById('exec-errors-tbody');
        const errorsPill = document.getElementById('exec-errors-count-pill');
        const transferErrors = status.transferErrors || [];

        if (errorsDetails && errorsTbody) {
            if (transferErrors.length > 0) {
                errorsDetails.style.display = 'block';
                if (errorsPill) errorsPill.textContent = `${transferErrors.length} Error${transferErrors.length > 1 ? 's' : ''}`;
                errorsTbody.innerHTML = transferErrors.map(err => {
                    const timeStr = err.timestampUtc ? new Date(err.timestampUtc).toLocaleTimeString() : '';
                    return `
                        <tr>
                            <td style="color:var(--muted); font-size:11px; white-space:nowrap;">${timeStr}</td>
                            <td style="white-space:nowrap;"><strong>${escapeHtml(err.sourceVolume)}</strong> → <strong>${escapeHtml(err.targetVolume)}</strong></td>
                            <td style="text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap;">${formatBytes(err.sizeOnDisk || 0)}</td>
                            <td style="word-break:break-all;" title="${escapeHtml(err.relativePath)}">
                                <strong>${escapeHtml(err.fileName)}</strong><br>
                                <span style="color:var(--muted); font-size:10px;">${escapeHtml(err.relativePath)}</span>
                            </td>
                            <td style="color:#b65b38; word-break:break-word;">${escapeHtml(err.errorMessage)}</td>
                        </tr>
                    `;
                }).join('');
            } else {
                errorsDetails.style.display = 'none';
                errorsTbody.innerHTML = '';
            }
        }
    },

    renderFolderCleanup(summary, containerEl) {
        if (!containerEl) return;
        if (!summary) {
            containerEl.style.display = 'none';
            containerEl.innerHTML = '';
            return;
        }
        containerEl.style.display = 'block';
        if (window.Balancer?.plan?.renderFolderCleanup) {
            window.Balancer.plan.renderFolderCleanup(summary, containerEl);
        }
    },

    renderSection(containerEl, options = {}) {
        const el = typeof containerEl === 'string' ? document.getElementById(containerEl) : containerEl;
        if (!el) return;

        const openAttr = options.open ? 'open' : '';
        const containerClass = options.containerClass ? ` class="${options.containerClass}"` : '';
        const titleText = options.titleText || '⚡ Plan Execution';
        const isSimulation = Boolean(options.isSimulation);
        let badgeHtml = options.badgeHtml || '';
        if (!badgeHtml && isSimulation) {
            badgeHtml = '<span style="background:#b65b38; color:white; font-size:10px; font-weight:800; padding:2px 8px; border-radius:4px; letter-spacing:0.5px;">SIMULATED ONLY</span>';
        }
        const subtitleText = options.subtitleText || '';
        const showNotice = options.showNotice ?? true;

        let controlsHtml = options.controlsHtml || '';
        if (!controlsHtml && options.showControls) {
            controlsHtml = `
                <button class="btn danger" id="btn-exec-cancel" onclick="event.stopPropagation(); ${options.onCancel || 'cancelSimExecution()'}" style="display:none;" title="Cancel currently running simulation">Cancel Simulation</button>
                <button class="btn primary" id="btn-exec-start" onclick="event.stopPropagation(); document.getElementById('details-exec').open = true; ${options.onStart || 'startSimExecution()'};" title="Run simulated file balancing execution">▶ Run Simulation</button>
            `;
        }

        let toolbarHtml = options.toolbarHtml || '';
        if (!toolbarHtml && options.showSimulationToolbar) {
            toolbarHtml = `
                <!-- Simulation Settings Toolbar -->
                <div class="toolbar" style="margin-bottom:16px; background:#fbfcf9; border:1px solid var(--line); border-radius:6px; padding:12px 16px; display:flex; flex-wrap:wrap; gap:16px; align-items:flex-end;">
                    <div class="field" style="flex:1; min-width:220px; align-self:center;">
                        <span style="font-size:12px; color:var(--muted);">
                            Uses snapshot selected in <strong>Placement Planning</strong> above.
                        </span>
                    </div>
                    <div class="field" style="width:160px;">
                        <div class="field-header" title="Maximum concurrent worker threads for this simulation">Nb of threads for Sim</div>
                        <input type="number" id="sim-threads" min="1" max="16" value="2">
                    </div>
                    <div class="field" style="width:180px;">
                        <div class="field-header" title="Simulated time scale for file balancing">Sim Duration</div>
                        <select id="sim-duration">
                            <option value="0">0s (Instant / No delay)</option>
                            <option value="30">30 seconds</option>
                            <option value="60">1 minute</option>
                            <option value="120" selected>2 minutes (Default)</option>
                            <option value="300">5 minutes</option>
                            <option value="600">10 minutes</option>
                        </select>
                    </div>
                </div>
            `;
        }

        const placeholderClassAttr = options.placeholderClass ? ` class="${options.placeholderClass}"` : '';
        const placeholderStyleAttr = options.placeholderStyle ? ` style="${options.placeholderStyle}"` : (options.placeholderClass ? '' : ' style="text-align:center; color:var(--muted); padding:20px;"');
        const placeholderText = options.placeholderText || (isSimulation ? "No simulation running. Select a snapshot and click 'Run Simulation'." : 'No execution running.');

        el.innerHTML = `
            <details id="details-exec"${containerClass} ${openAttr}>
                <summary style="display:flex; justify-content:space-between; align-items:center; gap:12px; flex-wrap:wrap;">
                    <div class="summary-title" style="display:flex; align-items:center; gap:10px;">
                        <span>${titleText}</span>
                        ${badgeHtml}
                        <span class="summary-subtitle" id="exec-subtitle">${subtitleText}</span>
                    </div>
                    ${controlsHtml ? `<div style="display:flex; align-items:center; gap:8px;" onclick="event.stopPropagation();">${controlsHtml}</div>` : ''}
                </summary>
                <div class="content">
                    ${toolbarHtml}
                    <!-- Summary bar with progress stats -->
                    <div class="exec-summary-bar" id="exec-summary-bar" style="display:none;">
                        <div class="exec-stat">
                            <span class="exec-stat-label">Progress</span>
                            <span class="exec-stat-value" id="exec-pct">0%</span>
                        </div>
                        <div class="exec-stat">
                            <span class="exec-stat-label">Transferred</span>
                            <span class="exec-stat-value" id="exec-transferred">0 B / 0 B</span>
                        </div>
                        <div class="exec-stat">
                            <span class="exec-stat-label">Files</span>
                            <span class="exec-stat-value" id="exec-files">0 / 0</span>
                        </div>
                        <div class="exec-stat">
                            <span class="exec-stat-label">Transfer Speed</span>
                            <span class="exec-stat-value" id="exec-speed">0 MB/s</span>
                        </div>
                        <div class="exec-stat">
                            <span class="exec-stat-label">Folders Cleaned</span>
                            <span class="exec-stat-value" id="exec-folders-cleaned">-</span>
                        </div>
                        <div class="exec-progress-container">
                            <div class="exec-progress-fill" id="exec-progress-fill"></div>
                        </div>
                    </div>

                    ${showNotice ? '<div id="exec-notice" class="notice"></div>' : ''}

                    <!-- Execution table with live dual bars and active disk indicator -->
                    <table class="plan-table" id="exec-table" style="width:100%; margin-top:10px;">
                        <thead>
                            <tr>
                                <th style="width: 110px;" title="Volume and physical disk participating in balancing">Volume</th>
                                <th style="width: 80px; text-align:right;" title="Usable capacity of the volume">Capacity</th>
                                <th style="width: auto; min-width: 260px; padding-left: 12px; padding-right: 12px;" title="Visual progression of outgoing data (-OUT) and incoming data (+IN) absorbing into the volume">START (-OUT) vs END (+IN)</th>
                                <th style="width: 240px;" title="Live disk reading or writing activity with throughput">Current Activity</th>
                                <th style="width: 140px; text-align:right;" title="Remaining data to be moved out (-OUT) and moved in (+IN)">Data Remaining</th>
                                <th style="width: 90px; text-align:right;" title="Remaining files to be moved out (-OUT) and moved in (+IN)">Files Remaining</th>
                            </tr>
                        </thead>
                        <tbody id="exec-tbody">
                            <tr><td colspan="6"${placeholderClassAttr}${placeholderStyleAttr}>${placeholderText}</td></tr>
                        </tbody>
                    </table>

                    <!-- Transfer Errors / Skipped Files Section -->
                    <details id="details-exec-errors" class="sub-details" style="margin-top:18px; display:none;" open>
                        <summary style="display:flex; justify-content:space-between; align-items:center;">
                            <div style="display:flex; align-items:center; gap:8px;">
                                <span style="font-weight:700; color:#b65b38;">⚠️ Transfer Errors / Skipped Files</span>
                                <span class="state-pill state-failed" id="exec-errors-count-pill" style="font-size:10px; padding:2px 8px;">0 Errors</span>
                            </div>
                            <span style="font-size:11px; color:var(--muted);">Files kept untouched on source drive due to locks or I/O errors</span>
                        </summary>
                        <div class="sub-content" style="padding:10px 0 0 0;">
                            <table class="plan-table" id="exec-errors-table" style="width:100%; font-size:11px;">
                                <thead>
                                    <tr>
                                        <th style="width:90px;">Time</th>
                                        <th style="width:90px;">Route</th>
                                        <th style="width:80px; text-align:right;">Size</th>
                                        <th>File & Relative Path</th>
                                        <th>Error Reason</th>
                                    </tr>
                                </thead>
                                <tbody id="exec-errors-tbody">
                                </tbody>
                            </table>
                        </div>
                    </details>
                </div>
            </details>
        `;
    }
};

window.Balancer.executor = window.Balancer.execution;
window.Balancer.executer = window.Balancer.execution;
