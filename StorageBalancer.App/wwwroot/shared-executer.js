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

// Executer Subsystem
window.Balancer.execution = {
    async start(options = {}) {
        const res = await fetch('/api/execution/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                snapshotName: options.snapshotName || null,
                isSimulation: options.isSimulation !== false,
                simulationDurationSeconds: (options.simulationDurationSeconds !== undefined && options.simulationDurationSeconds !== null) ? options.simulationDurationSeconds : 120,
                maxThreads: options.maxThreads || null,
                verifyCopies: options.verifyCopies !== false
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
                transferByVol.set(t.sourceVolume, { ...t, role: 'Reading' });
            }
            if (t.targetVolume && !transferByVol.has(t.targetVolume)) {
                transferByVol.set(t.targetVolume, { ...t, role: 'Writing' });
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
                const isWriting = active.role === 'Writing';
                const dotClass = isWriting ? 'dot-scan' : 'dot-scan';
                const dotColor = isWriting ? '#2085ec' : 'var(--green)';
                const speedText = active.throughputBps > 0 ? `(${formatBytes(active.throughputBps)}/s)` : '';
                activityHtml = `
                    <div style="display:flex; align-items:center; gap:6px; overflow:hidden;" title="${escapeHtml(active.fileName)}">
                        <span class="status-dot ${dotClass}" style="background:${dotColor}; flex-shrink:0;"></span>
                        <div style="white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font-size:12px;">
                            <strong>${active.role}:</strong> "${escapeHtml(active.fileName)}" <span style="color:var(--muted); font-size:11px;">${speedText}</span>
                        </div>
                    </div>
                `;
            } else if (isAllDone && (vol.movedOutTotalFiles > 0 || vol.movedInTotalFiles > 0)) {
                activityHtml = `<span class="state-pill state-complete">Complete</span>`;
            } else if (vol.movedOutTotalFiles === 0 && vol.movedInTotalFiles === 0) {
                activityHtml = `<span class="state-text" style="color:var(--muted); font-size:12px;">Balanced</span>`;
            } else {
                activityHtml = `<span class="state-text"><span class="status-dot dot-wait"></span>Idle</span>`;
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
    },

    renderFolderCleanup(summary, containerEl) {
        if (!containerEl) return;
        if (!summary) {
            containerEl.style.display = 'none';
            containerEl.innerHTML = '';
            return;
        }

        containerEl.style.display = 'block';
        const escapeHtml = window.Balancer.escapeHtml;

        const totalEvaluated = summary.totalFoldersEvaluated || 0;
        const totalInstances = summary.totalFolderInstances || (summary.cleanedCount + summary.preservedUniqueCount + summary.keptWithDataCount);
        const cleanedCount = summary.cleanedCount || 0;
        const preservedCount = summary.preservedUniqueCount || 0;
        const keptDataCount = summary.keptWithDataCount || 0;

        let showDetails = containerEl.dataset.showDetails === 'true';
        let activeFilter = containerEl.dataset.filter || 'Cleaned';
        let searchQuery = containerEl.dataset.search || '';
        let searchTimeout = null;

        async function fetchAndRenderTable(tbodyEl, statusNoteEl) {
            tbodyEl.innerHTML = `<tr><td colspan="5" style="text-align:center; padding:24px; color:var(--muted);"><span class="spinner" style="width:14px; height:14px; margin-right:8px;"></span> Loading folder details...</td></tr>`;
            try {
                const params = new URLSearchParams();
                if (activeFilter && activeFilter !== 'All') params.set('status', activeFilter);
                if (searchQuery) params.set('search', searchQuery);
                params.set('limit', '500');

                const res = await fetch(`/api/execution/folder-cleanup?${params.toString()}`);
                if (!res.ok) throw new Error('Failed to load details');
                const data = await res.json();
                const items = data.items || [];
                const totalMatching = data.total || 0;

                if (items.length === 0) {
                    tbodyEl.innerHTML = `<tr><td colspan="5" style="text-align:center; padding:24px; color:var(--muted);">No folders matching current filter.</td></tr>`;
                    if (statusNoteEl) statusNoteEl.textContent = '';
                    return;
                }

                tbodyEl.innerHTML = items.map(a => {
                    const cleanPath = (a.relativePath || '').replace(/^[\\\/]+/, '');
                    let statusBadge = '';
                    if (a.status === 'Cleaned') {
                        statusBadge = `<span style="background:#eaf8ef; color:#1b6e32; border:1px solid #c2e9cb; font-size:10px; font-weight:700; padding:2px 8px; border-radius:4px;">CLEANED</span>`;
                    } else if (a.status === 'PreservedUnique') {
                        statusBadge = `<span style="background:#edf4fc; color:#185fa5; border:1px solid #c7ddf5; font-size:10px; font-weight:700; padding:2px 8px; border-radius:4px;">PRESERVED UNIQUE</span>`;
                    } else {
                        statusBadge = `<span style="background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6; font-size:10px; font-weight:700; padding:2px 8px; border-radius:4px;">KEPT (DATA)</span>`;
                    }

                    return `
                        <tr>
                            <td style="font-family:monospace; font-size:11px; color:var(--ink); word-break:break-all;">
                                📁 ${escapeHtml(cleanPath)}
                            </td>
                            <td style="text-align:center;">
                                <strong style="font-size:12px;">${escapeHtml(a.volumeAlias)}</strong>
                            </td>
                            <td style="text-align:center;">
                                <span style="font-size:12px; color:var(--muted);">${escapeHtml(a.primaryVolumeAlias)}</span>
                            </td>
                            <td>
                                ${statusBadge}
                            </td>
                            <td style="color:var(--muted); font-size:11px;">
                                ${escapeHtml(a.reason)}
                            </td>
                        </tr>
                    `;
                }).join('');

                if (statusNoteEl) {
                    if (totalMatching > items.length) {
                        statusNoteEl.textContent = `Showing top ${items.length.toLocaleString()} of ${totalMatching.toLocaleString()} matching records. Use the search input to narrow results.`;
                    } else {
                        statusNoteEl.textContent = `Showing all ${totalMatching.toLocaleString()} matching records.`;
                    }
                }
            } catch (err) {
                tbodyEl.innerHTML = `<tr><td colspan="5" style="text-align:center; padding:20px; color:var(--red);">Failed to load folder records: ${escapeHtml(err.message)}</td></tr>`;
                if (statusNoteEl) statusNoteEl.textContent = '';
            }
        }

        function renderUI() {
            containerEl.innerHTML = `
                <div style="margin-top:24px; padding-top:20px; border-top:1px solid var(--line);">
                    <div style="display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:12px; margin-bottom:14px;">
                        <div>
                            <strong style="font-size:14px; color:var(--deep); display:flex; align-items:center; gap:8px;">
                                <span>📁</span> Empty Folder Cleanup
                            </strong>
                            <div style="font-size:12px; color:var(--muted); margin-top:2px;">
                                Evaluated ${totalEvaluated.toLocaleString()} logical folder paths across ${totalInstances.toLocaleString()} volume instances
                            </div>
                        </div>
                        <div style="display:flex; align-items:center; gap:8px; flex-wrap:wrap;">
                            <span class="state-pill state-complete" style="font-size:11px; padding:3px 10px; background:#eaf8ef; color:#1b6e32; border:1px solid #c2e9cb;">
                                Cleaned: ${cleanedCount.toLocaleString()}
                            </span>
                            <span class="state-pill" style="font-size:11px; padding:3px 10px; background:#edf4fc; color:#185fa5; border:1px solid #c7ddf5;">
                                Preserved Unique: ${preservedCount.toLocaleString()}
                            </span>
                            <span class="state-pill" style="font-size:11px; padding:3px 10px; background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6;">
                                Kept with Data: ${keptDataCount.toLocaleString()}
                            </span>
                        </div>
                    </div>

                    <!-- Filter Controls -->
                    <div style="display:flex; justify-content:space-between; align-items:center; gap:12px; flex-wrap:wrap; margin-bottom:12px;">
                        <div class="cleanup-pills" style="display:flex; gap:6px; flex-wrap:wrap;">
                            <button type="button" class="btn ${activeFilter === 'Cleaned' ? 'primary' : ''}" data-status="Cleaned" style="font-size:12px; padding:4px 10px;">
                                Cleaned (${cleanedCount.toLocaleString()})
                            </button>
                            <button type="button" class="btn ${activeFilter === 'PreservedUnique' ? 'primary' : ''}" data-status="PreservedUnique" style="font-size:12px; padding:4px 10px;">
                                Preserved Unique (${preservedCount.toLocaleString()})
                            </button>
                            <button type="button" class="btn ${activeFilter === 'KeptWithData' ? 'primary' : ''}" data-status="KeptWithData" style="font-size:12px; padding:4px 10px;">
                                Kept with Data (${keptDataCount.toLocaleString()})
                            </button>
                            <button type="button" class="btn ${activeFilter === 'All' ? 'primary' : ''}" data-status="All" style="font-size:12px; padding:4px 10px;">
                                All (${totalInstances.toLocaleString()})
                            </button>
                        </div>
                        <div style="display:flex; align-items:center; gap:8px;">
                            <input type="text" id="cleanup-search" placeholder="Search path, volume, reason..." value="${escapeHtml(searchQuery)}" style="font-size:12px; padding:5px 10px; width:240px; border:1px solid var(--line); border-radius:4px;">
                        </div>
                    </div>

                    <!-- Actions Table -->
                    <div style="max-height:420px; overflow-y:auto; border:1px solid var(--line); border-radius:6px; background:#fff;">
                        <table class="plan-table" style="margin:0; width:100%; font-size:12px;">
                            <thead>
                                <tr style="position:sticky; top:0; background:#f4f6f2; z-index:2;">
                                    <th style="width:auto;">Relative Folder Path</th>
                                    <th style="width:90px; text-align:center;">Volume</th>
                                    <th style="width:110px; text-align:center;">Primary Keeper</th>
                                    <th style="width:130px;">Action Status</th>
                                    <th style="width:auto;">Safety Verification / Reason</th>
                                </tr>
                            </thead>
                            <tbody id="cleanup-tbody"></tbody>
                        </table>
                    </div>
                    <div id="cleanup-status-note" style="font-size:11px; color:var(--muted); text-align:center; margin-top:8px;"></div>
                </div>
            `;

            const tbodyEl = containerEl.querySelector('#cleanup-tbody');
            const noteEl = containerEl.querySelector('#cleanup-status-note');
            fetchAndRenderTable(tbodyEl, noteEl);

            containerEl.querySelectorAll('.cleanup-pills button').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    activeFilter = btn.dataset.status;
                    containerEl.dataset.filter = activeFilter;
                    renderUI();
                });
            });

            const searchInput = containerEl.querySelector('#cleanup-search');
            if (searchInput) {
                searchInput.addEventListener('input', (e) => {
                    searchQuery = e.target.value.trim();
                    containerEl.dataset.search = searchQuery;
                    clearTimeout(searchTimeout);
                    searchTimeout = setTimeout(() => {
                        fetchAndRenderTable(tbodyEl, noteEl);
                    }, 250);
                });
            }
        }

        renderUI();
    }
};
