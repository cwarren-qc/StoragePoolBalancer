/**
 * Storage Pool Balancer - Shared Planner Module
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

// Planner Subsystem
window.Balancer.plan = {
    async loadSnapshots(selectEl, defaultOptionLabel = 'No snapshots found') {
        if (!selectEl) return [];
        const escapeHtml = window.Balancer.escapeHtml;
        try {
            const res = await fetch('/api/snapshots');
            const snaps = await res.json();
            if (snaps && snaps.length) {
                selectEl.innerHTML = snaps.map(s =>
                    `<option value="${escapeHtml(s.name)}">${escapeHtml(s.name)} (${new Date(s.lastModifiedUtc).toLocaleString()})</option>`
                ).join('');
            } else {
                selectEl.innerHTML = `<option value="">${escapeHtml(defaultOptionLabel)}</option>`;
            }
            return snaps || [];
        } catch (err) {
            selectEl.innerHTML = `<option value="">Error loading snapshots</option>`;
            return [];
        }
    },

    async generatePlan(snapshotName, includeFiles = false) {
        if (!snapshotName) throw new Error('Choose a snapshot.');
        const res = await fetch('/api/plan', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ snapshotName, includeFiles })
        });
        const plan = await res.json();
        if (!res.ok) throw new Error(plan.Error || plan.error || 'Failed to generate plan');
        return plan;
    },

    renderPlan(plan, containerEl, initialOptions = {}) {
        if (!containerEl || !plan) return;
        const escapeHtml = window.Balancer.escapeHtml;
        const formatBytes = window.Balancer.formatBytes;

        const sectionsOpen = initialOptions.sectionsOpenByDefault ?? true;

        const state = {
            plan,
            containerEl,
            sortCol: initialOptions.sortCol || 'path',
            sortDesc: initialOptions.sortDesc || false,
            showStayedIntact: initialOptions.showStayedIntact || false,
            volumeColors: window.Balancer.getVolumeColorMap(plan.volumes || [])
        };

        const totalMoved = (plan.placements || []).reduce((sum, p) => sum + (p.sizeMoved || 0), 0);

        let warningsHtml = '';
        if (plan.warnings && plan.warnings.length) {
            warningsHtml = plan.warnings.map(w =>
                `<div style="background:#f4edda; color:#775829; padding:8px 12px; border-radius:4px; font-size:12px; margin-bottom:12px;">${escapeHtml(w.message)}</div>`
            ).join('');
        }

        function computeVolumeTransfers(volumes) {
            const map = new Map();
            (volumes || []).forEach(v => {
                map.set(v.alias, {
                    alias: v.alias,
                    stayedSize: 0,
                    incoming: [],
                    outgoing: [],
                    finalSize: v.finalSize,
                    capacity: v.capacity
                });
            });

            (volumes || []).forEach(targetVol => {
                const targetData = map.get(targetVol.alias);
                (targetVol.provenance || []).forEach(prov => {
                    if (prov.alias === targetVol.alias) {
                        targetData.stayedSize = prov.size;
                    } else {
                        targetData.incoming.push({ alias: prov.alias, size: prov.size });
                        const sourceData = map.get(prov.alias);
                        if (sourceData) {
                            sourceData.outgoing.push({ alias: targetVol.alias, size: prov.size });
                        }
                    }
                });
            });

            map.forEach(data => {
                const outgoingTotal = data.outgoing.reduce((sum, o) => sum + o.size, 0);
                data.startSize = data.stayedSize + outgoingTotal;
                data.incoming.sort((a, b) => b.size - a.size);
                data.outgoing.sort((a, b) => b.size - a.size);
            });

            return map;
        }

        const volumes = plan.volumes || [];
        const volumeTransfers = computeVolumeTransfers(volumes);

        let totalCapacity = 0;
        let totalStartUsed = 0;
        let totalFinalUsed = 0;
        let totalFilesMoving = 0;
        let overCapacityCount = 0;
        let volumesWithMoves = 0;

        const startPcts = [];
        const finalPcts = [];

        volumes.forEach(v => {
            const transferData = volumeTransfers.get(v.alias) || { startSize: 0, outgoing: [], incoming: [] };
            const cap = v.capacity || 0;
            const startUsed = transferData.startSize || 0;
            const finalUsed = v.finalSize || 0;

            totalCapacity += cap;
            totalStartUsed += startUsed;
            totalFinalUsed += finalUsed;

            const filesIn = v.filesMovedIn || v.FilesMovedIn || 0;
            totalFilesMoving += filesIn;

            if (cap > 0) {
                const sPct = (startUsed / cap) * 100;
                const fPct = (finalUsed / cap) * 100;
                startPcts.push({ alias: v.alias, pct: sPct });
                finalPcts.push({ alias: v.alias, pct: fPct });
                if (fPct > 100) overCapacityCount++;
            }

            if ((transferData.outgoing && transferData.outgoing.length > 0) ||
                (transferData.incoming && transferData.incoming.length > 0)) {
                volumesWithMoves++;
            }
        });

        const poolFinalPct = totalCapacity > 0 ? (totalFinalUsed / totalCapacity) * 100 : 0;
        const poolStartPct = totalCapacity > 0 ? (totalStartUsed / totalCapacity) * 100 : 0;

        const minFinalPct = finalPcts.length ? Math.min(...finalPcts.map(x => x.pct)) : 0;
        const maxFinalPct = finalPcts.length ? Math.max(...finalPcts.map(x => x.pct)) : 0;
        const minStartPct = startPcts.length ? Math.min(...startPcts.map(x => x.pct)) : 0;
        const maxStartPct = startPcts.length ? Math.max(...startPcts.map(x => x.pct)) : 0;

        const placements = plan.placements || [];
        let pathsMovingCount = 0;
        let pathsIntactCount = 0;
        let movedCount = 0;
        let consolidatedCount = 0;
        let splitCount = 0;
        let intactSize = 0;

        placements.forEach(p => {
            if (p.logicApplied === 'Stayed intact' || (p.sizeMoved === 0 && p.logicApplied !== 'Consolidated' && p.logicApplied !== 'Split')) {
                pathsIntactCount++;
                intactSize += (p.sizeOnDisk || 0);
            } else {
                pathsMovingCount++;
                if (p.logicApplied === 'Consolidated' || p.logicApplied === 'Moved/Consolidated') {
                    consolidatedCount++;
                } else if (p.logicApplied === 'Split') {
                    splitCount++;
                } else {
                    movedCount++;
                }
            }
        });
        const totalPathsCount = placements.length;

        containerEl.innerHTML = `
            <div style="display:flex; justify-content:space-between; align-items:center; border-bottom:1px solid var(--line); padding-bottom:12px; margin-bottom:16px;">
                <span style="font-size:15px;"><strong>${formatBytes(totalMoved)}</strong> to transfer</span>
                <span style="font-size:12px; color:var(--muted);">Snapshot scanned: ${new Date(plan.snapshotScannedAt).toLocaleString()}</span>
            </div>
            ${warningsHtml}

            <!-- COLLAPSIBLE SECTION 1: VOLUME UTILIZATION -->
            <details class="sub-details" id="plan-details-volumes" ${sectionsOpen ? 'open' : ''}>
                <summary style="cursor:pointer; display:flex; justify-content:space-between; align-items:center;">
                    <span style="font-weight:700;" title="Volume storage capacity and simulated utilization before and after balancing">Volume Utilization</span>
                    <div style="display:flex; align-items:center; gap:8px;">
                        ${overCapacityCount > 0 ? `
                            <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#fde8e8; color:#9b1c1c; border:1px solid #f8b4b4;" title="Warning: ${overCapacityCount} volume(s) projected to exceed 100% capacity!">
                                Over Capacity: ${overCapacityCount}
                            </span>
                        ` : ''}
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#edf4fc; color:#185fa5; border:1px solid #c7ddf5;" title="Total pool utilization: ${formatBytes(totalFinalUsed)} used of ${formatBytes(totalCapacity)} capacity (${poolFinalPct.toFixed(1)}%) across ${volumes.length} volume(s)">
                            Pool: ${poolFinalPct.toFixed(1)}%
                        </span>
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#f4f0fa; color:#5b3296; border:1px solid #dfd4f2;" title="Projected volume utilization spread: ${minFinalPct.toFixed(1)}% to ${maxFinalPct.toFixed(1)}% (initial: ${minStartPct.toFixed(1)}% to ${maxStartPct.toFixed(1)}%)">
                            Spread: ${minFinalPct.toFixed(0)}% – ${maxFinalPct.toFixed(0)}%
                        </span>
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; ${totalFilesMoving > 0 ? 'background:#eaf8ef; color:#1b6e32; border:1px solid #c2e9cb;' : 'background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6;'}" title="${totalFilesMoving.toLocaleString()} files moving across ${volumesWithMoves} of ${volumes.length} volume(s)">
                            Files Moving: ${totalFilesMoving.toLocaleString()}
                        </span>
                    </div>
                </summary>
                <div class="sub-content" style="padding:16px 12px; overflow-x:auto;">
                    <table class="scan-table">
                        <thead>
                            <tr>
                                <th style="width: 75px;" title="Configured volume alias">Volume</th>
                                <th style="width: 85px; text-align:right;" title="Total usable storage capacity of this volume">Capacity</th>
                                <th style="width: auto; padding-left: 12px; padding-right: 12px;" title="Two proportional capacity bars: START (-OUT) shows current files and outgoing moves; END (+IN) shows target files and incoming moves. Free space exceeding 30% has a cut break.">Distribution (Start vs End)</th>
                                <th style="width: 85px; text-align:right;" title="Top: Initial used size before balancing. Bottom: Final projected size after balancing.">Used Size</th>
                                <th style="width: 60px; text-align:right;" title="Top: Initial % capacity used. Bottom: Final % capacity used.">% Util</th>
                                <th style="width: 95px; text-align:right;" title="Top: Outgoing data moving to other volumes (-OUT). Bottom: Incoming data moving from other volumes (+IN).">Data Moving</th>
                                <th style="width: 90px; text-align:right;" title="Top: Files moving out to other volumes (-OUT). Bottom: Files moving in from other volumes (+IN).">Files Moving</th>
                            </tr>
                        </thead>
                        <tbody class="plan-volume-tbody"></tbody>
                    </table>
                </div>
            </details>

            <!-- COLLAPSIBLE SECTION 2: PATH PLACEMENTS -->
            <details class="sub-details" id="plan-details-placements" ${sectionsOpen ? 'open' : ''} style="margin-top:16px;">
                <summary style="cursor:pointer; display:flex; justify-content:space-between; align-items:center;">
                    <span style="font-weight:700;" title="Folder placement decisions and file balancing rules">Path Placements</span>
                    <div style="display:flex; align-items:center; gap:8px;">
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; ${pathsMovingCount > 0 ? 'background:#eaf8ef; color:#1b6e32; border:1px solid #c2e9cb;' : 'background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6;'}" title="${pathsMovingCount.toLocaleString()} paths require file movements (${formatBytes(totalMoved)} total): ${movedCount} moved, ${consolidatedCount} consolidated, ${splitCount} split">
                            Moving: ${pathsMovingCount.toLocaleString()}
                        </span>
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6;" title="${pathsIntactCount.toLocaleString()} paths stay intact (${formatBytes(intactSize)} already reside on target volumes)">
                            Intact: ${pathsIntactCount.toLocaleString()}
                        </span>
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#edf4fc; color:#185fa5; border:1px solid #c7ddf5;" title="${totalPathsCount.toLocaleString()} total paths evaluated (${pathsMovingCount} moving, ${pathsIntactCount} intact)">
                            Total: ${totalPathsCount.toLocaleString()}
                        </span>
                        <label class="plan-stayed-intact-toggle" style="font-size:12px; display:flex; align-items:center; gap:6px; cursor:pointer; color:var(--ink); font-weight:600; margin-left:4px;" title="Toggle visibility of paths where all files already reside on target volumes and require no file movement" onclick="event.stopPropagation();">
                            <input type="checkbox" class="plan-cb-intact" ${state.showStayedIntact ? 'checked' : ''}> Show 'Stayed intact'
                        </label>
                    </div>
                </summary>
                <div class="sub-content" style="padding:16px 12px; overflow-x:auto;">
                    <table class="plan-table" id="placement-table">
                        <thead>
                            <tr>
                                <th class="sortable" data-sort="order" style="width: 4%;" title="Rule evaluation order. Lower numbers run first; '-' denotes unplaced, duplicate, or catch-all files. Click to sort.">#</th>
                                <th class="sortable" data-sort="path" style="width: 38%;" title="Relative pool folder path or category. Special categories include ** Duplicate, ** Unmatched, and <Pool Root>. Click to sort.">Path</th>
                                <th class="sortable" data-sort="logic" style="width: 12%;" title="Placement strategy applied: Stayed intact, Moved, Split, Consolidated, or Moved/Consolidated. Click to sort.">Logic applied</th>
                                <th class="sortable" data-sort="target" style="width: 12%;" title="Destination volume(s) assigned to store files for this path. Click to sort.">Target volume</th>
                                <th class="sortable" data-sort="size" style="width: 10%; text-align:right;" title="Total size on disk of all files under this path. Click to sort.">Total Size</th>
                                <th class="sortable" data-sort="moved" style="width: 11%; text-align:right;" title="Amount of data that must be transferred between physical volumes to satisfy placement rules. Click to sort.">Size Moved</th>
                                <th style="width: 13%; text-align:left;" title="Visual distribution showing folder data sources. Leftmost segment shows data staying on the target volume; other segments show incoming moves from source volumes.">Distribution</th>
                            </tr>
                        </thead>
                        <tbody class="plan-placement-tbody"></tbody>
                    </table>
                </div>
            </details>
            <!-- COLLAPSIBLE SECTION 3: EMPTY FOLDER CLEANUP -->
            <details class="sub-details" id="plan-details-cleanup" ${sectionsOpen ? 'open' : ''} style="margin-top:16px; ${plan.folderCleanup ? '' : 'display:none;'}">
                <summary style="cursor:pointer; display:flex; justify-content:space-between; align-items:center;">
                    <span style="font-weight:700;" title="Safe empty folder cleanup and migration planned across pool volumes">Empty Folder Cleanup</span>
                    <div style="display:flex; align-items:center; gap:8px;">
                        <span class="state-pill state-complete" style="font-size:11px; padding:2px 8px; background:#eaf8ef; color:#1b6e32; border:1px solid #c2e9cb;" title="Empty folder copies on non-primary volumes that will be cleaned up">
                            Cleaned: ${(plan.folderCleanup?.cleanedCount || 0).toLocaleString()}
                        </span>
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#edf4fc; color:#185fa5; border:1px solid #c7ddf5;" title="Empty folders with no other copies that will be preserved on the primary volume">
                            Preserved: ${(plan.folderCleanup?.preservedUniqueCount || 0).toLocaleString()}
                        </span>
                        <span class="state-pill" style="font-size:11px; padding:2px 8px; background:#f3f4f2; color:#5b655f; border:1px solid #d8ddd6;" title="Folders containing active files or subfolders that will remain in place">
                            Kept: ${(plan.folderCleanup?.keptWithDataCount || 0).toLocaleString()}
                        </span>
                    </div>
                </summary>
                <div class="sub-content" id="plan-folder-cleanup-container" style="padding:16px 12px; overflow-x:auto;"></div>
            </details>
        `;

        function renderVolumeDualBars(startSegments, startUsed, endSegments, endUsed, capacity, volumeColors) {
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
        window.Balancer.renderVolumeDualBars = renderVolumeDualBars;

        function renderVolumes() {
            const tbody = containerEl.querySelector('.plan-volume-tbody');
            if (!tbody || !state.plan.volumes) return;

            const transferMap = computeVolumeTransfers(state.plan.volumes);
            const vols = [...state.plan.volumes].sort((a, b) => a.alias.localeCompare(b.alias, undefined, { numeric: true }));

            tbody.innerHTML = vols.map(volume => {
                const color = state.volumeColors.get(volume.alias) || '#89958f';
                const transferData = transferMap.get(volume.alias) || {
                    stayedSize: 0,
                    startSize: volume.finalSize,
                    incoming: [],
                    outgoing: []
                };

                const startSize = transferData.startSize;
                const finalSize = volume.finalSize;
                const capacity = volume.capacity;

                const startPct = capacity > 0 ? (startSize / capacity) * 100 : 0;
                const finalPct = capacity > 0 ? (finalSize / capacity) * 100 : 0;
                const isOver = finalPct > 100;

                const otherItemsSize = volume.otherItemsSizeOnDisk || volume.OtherItemsSizeOnDisk || 0;
                const poolStayedSize = Math.max(0, transferData.stayedSize - otherItemsSize);

                // --- 1. START BAR: OtherItems + Stayed pool items + Outgoing items to other volumes ---
                const startSegments = [];
                if (otherItemsSize > 0) {
                    startSegments.push({
                        alias: volume.alias,
                        size: otherItemsSize,
                        isOther: true,
                        tooltip: `${formatBytes(otherItemsSize)} Other items (outside PoolPart) on ${volume.alias}`
                    });
                }
                if (poolStayedSize > 0) {
                    startSegments.push({
                        alias: volume.alias,
                        size: poolStayedSize,
                        isOther: false,
                        tooltip: `${formatBytes(poolStayedSize)} pool files stay on ${volume.alias}`
                    });
                }
                transferData.outgoing.forEach(out => {
                    startSegments.push({
                        alias: out.alias,
                        size: out.size,
                        isOther: false,
                        tooltip: `${formatBytes(out.size)} moves to ${out.alias}`
                    });
                });

                // --- 2. END BAR: OtherItems + Stayed pool items + Incoming items from other volumes ---
                const endSegments = [];
                if (otherItemsSize > 0) {
                    endSegments.push({
                        alias: volume.alias,
                        size: otherItemsSize,
                        isOther: true,
                        tooltip: `${formatBytes(otherItemsSize)} Other items (outside PoolPart) on ${volume.alias}`
                    });
                }
                if (poolStayedSize > 0) {
                    endSegments.push({
                        alias: volume.alias,
                        size: poolStayedSize,
                        isOther: false,
                        tooltip: `${formatBytes(poolStayedSize)} pool files stayed on ${volume.alias}`
                    });
                }
                transferData.incoming.forEach(inc => {
                    endSegments.push({
                        alias: inc.alias,
                        size: inc.size,
                        isOther: false,
                        tooltip: `${formatBytes(inc.size)} moved from ${inc.alias}`
                    });
                });

                const { startBarHtml, endBarHtml } = renderVolumeDualBars(startSegments, startSize, endSegments, finalSize, capacity, state.volumeColors);

                const outgoingBytes = transferData.outgoing.reduce((sum, o) => sum + o.size, 0);
                const incomingBytes = transferData.incoming.reduce((sum, i) => sum + i.size, 0);
                const filesOut = volume.filesMovedOut || volume.FilesMovedOut || 0;
                const filesIn = volume.filesMovedIn || volume.FilesMovedIn || 0;

                const stateWarning = volume.isEligible ? '' : `<div style="color:var(--orange); font-size:10px; margin-top:2px;">${escapeHtml(volume.status)}</div>`;

                return `
                    <tr>
                        <td>
                            <div style="display:flex; align-items:center; gap:8px;">
                                <span style="width:13px; height:13px; border-radius:50%; background:${color}; flex-shrink:0; box-shadow: inset 0 0 0 1px rgba(0,0,0,0.1);"></span>
                                <div>
                                    <strong style="font-size:13px;">${escapeHtml(volume.alias)}</strong>
                                    ${stateWarning}
                                </div>
                            </div>
                        </td>
                        <td style="text-align:right; font-variant-numeric:tabular-nums; font-weight:600; color:var(--ink);">
                            ${formatBytes(capacity)}
                        </td>
                        <td style="padding-left:12px; padding-right:12px;">
                            <div class="dual-bar-container">
                                <div class="bar-row">
                                    <span class="bar-stage-tag" title="Initial volume state and files moving out to other volumes">START (-OUT)</span>
                                    ${startBarHtml}
                                </div>
                                <div class="bar-row">
                                    <span class="bar-stage-tag" title="Final projected volume state and files moving in from other volumes">END (+IN)</span>
                                    ${endBarHtml}
                                </div>
                            </div>
                        </td>
                        <td style="text-align:right;">
                            <div class="dual-val-container">
                                <div class="val-row val-dim" title="Initial used size: ${formatBytes(startSize)}">${formatBytes(startSize)}</div>
                                <div class="val-row val-strong" title="Final used size: ${formatBytes(finalSize)}">${formatBytes(finalSize)}</div>
                            </div>
                        </td>
                        <td style="text-align:right;">
                            <div class="dual-val-container">
                                <div class="val-row val-dim" title="Initial utilization: ${startPct.toFixed(1)}%">${startPct.toFixed(0)}%</div>
                                <div class="val-row ${isOver ? 'val-out' : 'val-strong'}" title="Final utilization: ${finalPct.toFixed(1)}%">${finalPct.toFixed(0)}%</div>
                            </div>
                        </td>
                        <td style="text-align:right;">
                            <div class="dual-val-container">
                                <div class="val-row ${outgoingBytes > 0 ? 'val-out' : 'val-dim'}" title="Outgoing data to other volumes: ${formatBytes(outgoingBytes)}">
                                    ${outgoingBytes > 0 ? `-${formatBytes(outgoingBytes)}` : '-'}
                                </div>
                                <div class="val-row ${incomingBytes > 0 ? 'val-in' : 'val-dim'}" title="Incoming data from other volumes: ${formatBytes(incomingBytes)}">
                                    ${incomingBytes > 0 ? `+${formatBytes(incomingBytes)}` : '-'}
                                </div>
                            </div>
                        </td>
                        <td style="text-align:right;">
                            <div class="dual-val-container">
                                <div class="val-row ${filesOut > 0 ? 'val-out' : 'val-dim'}" title="Files moving out: ${filesOut > 0 ? filesOut.toLocaleString() : 'None'}">
                                    ${filesOut > 0 ? `-${filesOut.toLocaleString()}` : '-'}
                                </div>
                                <div class="val-row ${filesIn > 0 ? 'val-in' : 'val-dim'}" title="Files moving in: ${filesIn > 0 ? filesIn.toLocaleString() : 'None'}">
                                    ${filesIn > 0 ? `+${filesIn.toLocaleString()}` : '-'}
                                </div>
                            </div>
                        </td>
                    </tr>
                `;
            }).join('');
        }

        function renderPlacements() {
            const tbody = containerEl.querySelector('.plan-placement-tbody');
            if (!tbody) return;

            let filtered = state.plan.placements || [];
            if (!state.showStayedIntact) {
                filtered = filtered.filter(p => p.logicApplied !== 'Stayed intact');
            }

            filtered.sort((a, b) => {
                let valA, valB;
                switch (state.sortCol) {
                    case 'order': valA = a.order; valB = b.order; break;
                    case 'path': valA = a.relativePath.toLowerCase(); valB = b.relativePath.toLowerCase(); break;
                    case 'logic': valA = a.logicApplied.toLowerCase(); valB = b.logicApplied.toLowerCase(); break;
                    case 'target': valA = a.targets.length > 0 ? a.targets[0].alias : ''; valB = b.targets.length > 0 ? b.targets[0].alias : ''; break;
                    case 'size': valA = a.sizeOnDisk; valB = b.sizeOnDisk; break;
                    case 'moved': valA = a.sizeMoved; valB = b.sizeMoved; break;
                }

                let cmp = valA < valB ? -1 : (valA > valB ? 1 : 0);
                if (cmp === 0) {
                    const pathA = a.relativePath.toLowerCase();
                    const pathB = b.relativePath.toLowerCase();
                    return pathA < pathB ? -1 : (pathA > pathB ? 1 : 0);
                }
                return state.sortDesc ? -cmp : cmp;
            });

            containerEl.querySelectorAll('#placement-table th.sortable').forEach(th => {
                th.innerHTML = th.innerHTML.replace(/ ▾| ▴/g, '');
                if (th.dataset.sort === state.sortCol) {
                    th.innerHTML += state.sortDesc ? ' ▾' : ' ▴';
                }
            });

            if (filtered.length === 0) {
                tbody.innerHTML = '<tr><td colspan="7" class="plan-muted" style="text-align:center; padding:20px;">No placements to show. Check \'Show Stayed intact\' to see unchanged paths.</td></tr>';
                return;
            }

            tbody.innerHTML = filtered.map(item => {
                const isMoved = item.sizeMoved > 0;
                let targetUi = '';

                if (item.targets.length === 0) {
                    targetUi = '<span class="plan-muted">-</span>';
                } else if (item.logicApplied === 'Split' || item.targets.length > 1) {
                    const targetAlias = escapeHtml(item.targets[0].alias);
                    const targetColor = state.volumeColors.get(item.targets[0].alias) || '#89958f';
                    const segments = item.targets.map(t => {
                        const pct = (t.size / item.sizeOnDisk) * 100;
                        const tooltip = `${escapeHtml(t.alias)}: ${formatBytes(t.size)} (${pct.toFixed(1)}%)`;
                        return `<span class="provenance-segment" style="width:${pct}%; background:${state.volumeColors.get(t.alias) || '#89958f'}" title="${tooltip}"></span>`;
                    }).join('');
                    targetUi = `<div style="display:flex; flex-direction:column; gap:4px;">
                                  <span class="target-col"><i class="transfer-swatch" style="--source-color:${targetColor}"></i>${targetAlias}</span>
                                  <div class="provenance-bar" style="height:6px; margin:0; width:100px; padding:0; background:#e9eee3; border-radius:3px; display:flex; overflow:hidden;">${segments}</div>
                                </div>`;
                } else {
                    const t = item.targets[0];
                    const c = state.volumeColors.get(t.alias) || '#89958f';
                    targetUi = `<span class="target-col"><i class="transfer-swatch" style="--source-color:${c}"></i>${escapeHtml(t.alias)}</span>`;
                }

                let sizeMovedUi = '<span class="plan-muted">-</span>';
                if (isMoved) {
                    const pct = ((item.sizeMoved / item.sizeOnDisk) * 100).toFixed(1);
                    sizeMovedUi = `<div style="display:flex; flex-direction:column; align-items:flex-end; gap:2px; line-height:1.2;">
                                      <span style="font-variant-numeric:tabular-nums; white-space:nowrap;">${formatBytes(item.sizeMoved)}</span>
                                      <span style="font-size:10px; color:var(--muted); font-variant-numeric:tabular-nums; line-height:1;">(${pct}%)</span>
                                   </div>`;
                }

                let distributionUi = '<span class="plan-muted">-</span>';
                if (item.sizeOnDisk > 0 && item.sources && item.sources.length > 0) {
                    let displaySources = item.sources.slice();
                    if (item.targets && item.targets.length === 1) {
                        const targetAlias = item.targets[0].alias.toLowerCase();
                        displaySources.sort((a, b) => {
                            const aIsTarget = a.alias.toLowerCase() === targetAlias;
                            const bIsTarget = b.alias.toLowerCase() === targetAlias;
                            if (aIsTarget && !bIsTarget) return -1;
                            if (!aIsTarget && bIsTarget) return 1;
                            return b.size - a.size;
                        });
                    } else {
                        displaySources.sort((a, b) => b.size - a.size);
                    }

                    const segments = displaySources.map(src => {
                        const pct = (src.size / item.sizeOnDisk) * 100;
                        const srcColor = state.volumeColors.get(src.alias) || '#89958f';
                        let tooltip = '';
                        if (item.targets && item.targets.length === 1) {
                            const targetAlias = item.targets[0].alias;
                            if (src.alias.toLowerCase() === targetAlias.toLowerCase()) {
                                tooltip = `${escapeHtml(src.alias)} (Stayed in place): ${formatBytes(src.size)} (${pct.toFixed(1)}%)`;
                            } else {
                                tooltip = `${escapeHtml(src.alias)}: ${formatBytes(src.size)} (${pct.toFixed(1)}%)`;
                            }
                        } else {
                            tooltip = `${escapeHtml(src.alias)}: ${formatBytes(src.size)} (${pct.toFixed(1)}%)`;
                        }
                        return `<span class="provenance-segment" style="width:${pct}%; min-width:3px; background:${srcColor};" title="${tooltip}"></span>`;
                    });

                    if (segments.length > 0) {
                        distributionUi = `<div class="provenance-bar" style="height:7px; margin:4px 0 0 0; width:120px; padding:0; background:#e9eee3; border-radius:3px; display:flex; overflow:hidden;">${segments.join('')}</div>`;
                    }
                }

                const orderText = item.order > 0 ? item.order : '-';
                const orderTitle = `Placement decision order #${item.order}`;

                let pathTitle = `Pool folder: ${item.relativePath}`;
                if (item.relativePath === '** Duplicate') {
                    pathTitle = 'Duplicate copies of files. Preserved in place or balanced onto emptiest volumes.';
                } else if (item.relativePath === '** Unmatched') {
                    pathTitle = 'Files and folders that did not match any placement rule.';
                } else if (item.relativePath.includes('[duplicate]')) {
                    pathTitle = `Duplicate copy of folder: ${item.relativePath}`;
                } else if (item.relativePath.startsWith('<Pool Root>')) {
                    pathTitle = 'Loose files and folders residing directly at the root of the storage pool.';
                }

                const logicDescriptions = {
                    'Stayed intact': 'All files already reside on allowed target volumes and require no file movement.',
                    'Moved': 'All files under this path are assigned to move from their source volume to a target volume.',
                    'Split': 'Files under this path are distributed across multiple allowed target volumes based on depth and available capacity.',
                    'Consolidated': 'Files dispersed across multiple volumes are gathered onto the primary volume.',
                    'Moved/Consolidated': 'Files dispersed across multiple volumes are gathered onto a new single target volume.'
                };
                const logicTitle = logicDescriptions[item.logicApplied] || item.logicApplied;

                return `
                    <tr>
                        <td style="font-variant-numeric:tabular-nums; color:var(--muted);" title="${escapeHtml(orderTitle)}">${orderText}</td>
                        <td style="font-family:monospace; font-size:11px; overflow-wrap:anywhere;" title="${escapeHtml(pathTitle)}">${escapeHtml(item.relativePath)}</td>
                        <td><span class="reason-badge" title="${escapeHtml(logicTitle)}">${escapeHtml(item.logicApplied)}</span></td>
                        <td>${targetUi}</td>
                        <td style="text-align:right; font-variant-numeric:tabular-nums" title="Total data size on disk: ${formatBytes(item.sizeOnDisk)}">${formatBytes(item.sizeOnDisk)}</td>
                        <td style="text-align:right;" title="${isMoved ? `${formatBytes(item.sizeMoved)} moved between volumes` : 'No movement required'}">${sizeMovedUi}</td>
                        <td style="text-align:left;">${distributionUi}</td>
                    </tr>
                `;
            }).join('');
        }

        containerEl.querySelector('.plan-cb-intact')?.addEventListener('change', (e) => {
            state.showStayedIntact = e.target.checked;
            renderPlacements();
        });

        containerEl.querySelectorAll('#placement-table th.sortable').forEach(th => {
            th.addEventListener('click', () => {
                const sortKey = th.dataset.sort;
                if (state.sortCol === sortKey) {
                    state.sortDesc = !state.sortDesc;
                } else {
                    state.sortCol = sortKey;
                    state.sortDesc = false;
                }
                renderPlacements();
            });
        });

        renderVolumes();
        renderPlacements();

        const folderContainer = containerEl.querySelector('#plan-folder-cleanup-container');
        if (folderContainer && plan.folderCleanup) {
            window.Balancer.plan.renderFolderCleanup(plan.folderCleanup, folderContainer);
        }
    },

    renderFolderCleanup(summary, containerEl) {
        if (!containerEl) return;
        if (!summary) {
            containerEl.innerHTML = '<div style="color:var(--muted); font-size:12px; text-align:center; padding:16px;">No folder cleanup planned.</div>';
            return;
        }

        const escapeHtml = window.Balancer.escapeHtml;
        const totalEvaluated = summary.totalFoldersEvaluated || 0;
        const totalInstances = summary.totalFolderInstances || (summary.cleanedCount + summary.preservedUniqueCount + summary.keptWithDataCount);
        const cleanedCount = summary.cleanedCount || 0;
        const preservedCount = summary.preservedUniqueCount || 0;
        const keptDataCount = summary.keptWithDataCount || 0;

        let activeFilter = containerEl.dataset.filter || 'Cleaned';
        let searchQuery = containerEl.dataset.search || '';
        let searchTimeout = null;

        async function fetchAndRenderTable(tbodyEl, statusNoteEl) {
            tbodyEl.innerHTML = `<tr><td colspan="5" style="text-align:center; padding:24px; color:var(--muted);"><span class="spinner" style="width:14px; height:14px; margin-right:8px;"></span> Loading folder details...</td></tr>`;
            try {
                let items = [];
                let totalMatching = 0;

                if (summary.actions && summary.actions.length > 0) {
                    let filtered = summary.actions;
                    if (activeFilter && activeFilter !== 'All') {
                        filtered = filtered.filter(a => (a.status || '').toLowerCase() === activeFilter.toLowerCase());
                    }
                    if (searchQuery) {
                        const q = searchQuery.toLowerCase();
                        filtered = filtered.filter(a =>
                            (a.relativePath || '').toLowerCase().includes(q) ||
                            (a.volumeAlias || '').toLowerCase().includes(q) ||
                            (a.primaryVolumeAlias || '').toLowerCase().includes(q) ||
                            (a.reason || '').toLowerCase().includes(q)
                        );
                    }
                    totalMatching = filtered.length;
                    items = filtered.slice(0, 500);
                } else {
                    const params = new URLSearchParams();
                    if (activeFilter && activeFilter !== 'All') params.set('status', activeFilter);
                    if (searchQuery) params.set('search', searchQuery);
                    params.set('limit', '500');

                    const res = await fetch(`/api/plan/folder-cleanup?${params.toString()}`);
                    if (!res.ok) throw new Error('Failed to load details');
                    const data = await res.json();
                    items = data.items || [];
                    totalMatching = data.total || 0;
                }

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
                                📁 ${escapeHtml(cleanPath || '<Pool Root>')}
                            </td>
                            <td style="text-align:center;">
                                <strong style="font-size:12px;">${escapeHtml(a.volumeAlias)}</strong>
                            </td>
                            <td style="text-align:center;">
                                <span style="font-size:12px; color:var(--muted);">${escapeHtml(a.primaryVolumeAlias || '-')}</span>
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
                <div>
                    <div style="font-size:12px; color:var(--muted); margin-bottom:12px;">
                        Evaluated ${totalEvaluated.toLocaleString()} logical folder paths across ${totalInstances.toLocaleString()} volume instances
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
                            <input type="text" class="cleanup-search-input" placeholder="Search path, volume, reason..." value="${escapeHtml(searchQuery)}" style="font-size:12px; padding:5px 10px; width:240px; border:1px solid var(--line); border-radius:4px;">
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
                            <tbody class="cleanup-tbody"></tbody>
                        </table>
                    </div>
                    <div class="cleanup-status-note" style="font-size:11px; color:var(--muted); text-align:center; margin-top:8px;"></div>
                </div>
            `;

            const tbodyEl = containerEl.querySelector('.cleanup-tbody');
            const noteEl = containerEl.querySelector('.cleanup-status-note');
            fetchAndRenderTable(tbodyEl, noteEl);

            containerEl.querySelectorAll('.cleanup-pills button').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.preventDefault();
                    activeFilter = btn.dataset.status;
                    containerEl.dataset.filter = activeFilter;
                    renderUI();
                });
            });

            const searchInput = containerEl.querySelector('.cleanup-search-input');
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
