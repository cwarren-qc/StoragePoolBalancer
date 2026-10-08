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

// Executer Subsystem
window.Balancer.execution = {
    renderTable(volumeRows, tbodyEl) {
        if (!tbodyEl) return;
        const escapeHtml = window.Balancer.escapeHtml;
        const formatBytes = window.Balancer.formatBytes;

        if (!volumeRows || volumeRows.length === 0) {
            tbodyEl.innerHTML = '<tr><td colspan="6" style="text-align:center; color:var(--muted); padding:20px;">No execution data available.</td></tr>';
            return;
        }

        tbodyEl.innerHTML = volumeRows.map(vol => {
            let stateHtml = '';
            if (vol.status === 'Complete') {
                stateHtml = '<span class="state-pill state-complete">Complete</span>';
            } else if (vol.status === 'Transferring') {
                stateHtml = '<span class="state-text"><span class="status-dot dot-scan"></span>Transferring</span>';
            } else if (vol.status === 'Failed') {
                stateHtml = '<span class="state-pill state-failed">Failed</span>';
            } else {
                stateHtml = '<span class="state-text"><span class="status-dot dot-wait"></span>Idle</span>';
            }

            const filesDisplay = vol.totalFiles > 0
                ? `${Number(vol.filesTransferred).toLocaleString()} / ${Number(vol.totalFiles).toLocaleString()}`
                : Number(vol.filesTransferred).toLocaleString();

            const bytesDisplay = vol.totalBytes > 0
                ? `${formatBytes(vol.bytesTransferred)} / ${formatBytes(vol.totalBytes)}`
                : formatBytes(vol.bytesTransferred);

            const speedDisplay = vol.status === 'Transferring' && vol.speedBps > 0
                ? `${(vol.speedBps / (1024 * 1024)).toFixed(1)} MB/s`
                : '-';

            const rawPath = vol.currentFile || '-';

            return `
                <tr>
                    <td><strong>${escapeHtml(vol.alias)}</strong></td>
                    <td>${stateHtml}</td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums;">${filesDisplay}</td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums;">${bytesDisplay}</td>
                    <td style="text-align:right; font-variant-numeric:tabular-nums;">${speedDisplay}</td>
                    <td style="min-width:0; width:100%;">
                        <div style="white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font-family:monospace; font-size:11px; color:var(--muted);" title="${escapeHtml(rawPath)}">
                            ${escapeHtml(rawPath)}
                        </div>
                    </td>
                </tr>
            `;
        }).join('');
    },

    createSimulatedRunner(plan, { onProgress, onComplete, onError }) {
        let isRunning = true;
        let timerId = null;

        // Extract items that require file movement
        const volumes = (plan.volumes || []).map(v => ({
            alias: v.alias,
            status: 'Idle',
            filesTransferred: 0,
            totalFiles: 0,
            bytesTransferred: 0,
            totalBytes: 0,
            speedBps: 0,
            currentFile: '-'
        }));
        const volMap = new Map(volumes.map(v => [v.alias, v]));

        // Gather planned transfer items
        const transferItems = [];
        let totalPlanBytes = 0;

        (plan.placements || []).forEach(p => {
            if (p.sizeMoved > 0) {
                totalPlanBytes += p.sizeMoved;

                if (p.files && p.files.length > 0) {
                    p.files.forEach(f => {
                        if (f.originalVolumeAlias !== f.destinationVolumeAlias) {
                            transferItems.push({
                                path: f.relativePath,
                                size: f.sizeOnDisk || f.size || 1024,
                                source: f.originalVolumeAlias,
                                target: f.destinationVolumeAlias
                            });
                        }
                    });
                } else {
                    // Synthesize chunks if file breakdown wasn't requested
                    const targetAlias = p.targets && p.targets[0] ? p.targets[0].alias : volumes[0]?.alias;
                    const sourceAlias = p.sources && p.sources[0] ? p.sources[0].alias : (volumes[1]?.alias || volumes[0]?.alias);
                    const estChunkCount = Math.max(1, Math.min(20, Math.ceil(p.sizeMoved / (100 * 1024 * 1024))));
                    const chunkSize = Math.ceil(p.sizeMoved / estChunkCount);
                    for (let i = 0; i < estChunkCount; i++) {
                        transferItems.push({
                            path: `${p.relativePath}\\chunk_${i + 1}.dat`,
                            size: chunkSize,
                            source: sourceAlias,
                            target: targetAlias
                        });
                    }
                }
            }
        });

        // Set volume totals
        transferItems.forEach(item => {
            const src = volMap.get(item.source);
            if (src) {
                src.totalBytes += item.size;
                src.totalFiles += 1;
            }
            const dst = volMap.get(item.target);
            if (dst && dst !== src) {
                dst.totalBytes += item.size;
                dst.totalFiles += 1;
            }
        });

        const totalFiles = transferItems.length;
        let currentItemIndex = 0;
        let currentItemTransferred = 0;
        let overallTransferredBytes = 0;
        let lastTickTime = performance.now();

        if (totalPlanBytes === 0 || transferItems.length === 0) {
            // No files need to move!
            setTimeout(() => {
                volumes.forEach(v => { v.status = 'Complete'; v.currentFile = 'All files balanced'; });
                if (onProgress) {
                    onProgress({
                        progressPercent: 100,
                        transferredBytes: 0,
                        totalBytes: 0,
                        filesTransferred: 0,
                        totalFiles: 0,
                        speedBps: 0,
                        volumes,
                        activeFile: 'No transfers required'
                    });
                }
                if (onComplete) onComplete({ volumes, totalTransferred: 0 });
            }, 400);
            return { cancel: () => {} };
        }

        // Target around 8-12 seconds total execution time for realistic feel
        const estimatedBytesPerSecond = Math.max(50 * 1024 * 1024, Math.ceil(totalPlanBytes / 8));

        function tick() {
            if (!isRunning) return;

            const now = performance.now();
            const deltaSec = (now - lastTickTime) / 1000;
            lastTickTime = now;

            const bytesThisTick = Math.min(
                totalPlanBytes - overallTransferredBytes,
                Math.ceil(estimatedBytesPerSecond * deltaSec)
            );

            let remainingTickBytes = bytesThisTick;
            let activeItem = transferItems[currentItemIndex];

            while (remainingTickBytes > 0 && currentItemIndex < transferItems.length) {
                activeItem = transferItems[currentItemIndex];
                const itemNeeded = activeItem.size - currentItemTransferred;
                const take = Math.min(remainingTickBytes, itemNeeded);

                currentItemTransferred += take;
                overallTransferredBytes += take;
                remainingTickBytes -= take;

                // Update volume stats
                const src = volMap.get(activeItem.source);
                const dst = volMap.get(activeItem.target);
                if (src) {
                    src.bytesTransferred += take;
                    src.status = 'Transferring';
                    src.currentFile = activeItem.path;
                }
                if (dst) {
                    dst.bytesTransferred += take;
                    dst.status = 'Transferring';
                    dst.currentFile = activeItem.path;
                }

                if (currentItemTransferred >= activeItem.size) {
                    if (src) src.filesTransferred += 1;
                    if (dst && dst !== src) dst.filesTransferred += 1;
                    currentItemIndex++;
                    currentItemTransferred = 0;
                }
            }

            // Update speeds
            const speed = deltaSec > 0 ? (bytesThisTick / deltaSec) : 0;
            volumes.forEach(v => {
                if (v.status === 'Transferring') {
                    v.speedBps = speed;
                } else {
                    v.speedBps = 0;
                }
            });

            const pct = totalPlanBytes > 0
                ? Math.min(100, Math.round((overallTransferredBytes / totalPlanBytes) * 100))
                : 100;

            if (onProgress) {
                onProgress({
                    progressPercent: pct,
                    transferredBytes: overallTransferredBytes,
                    totalBytes: totalPlanBytes,
                    filesTransferred: currentItemIndex,
                    totalFiles,
                    speedBps: speed,
                    volumes,
                    activeFile: activeItem ? activeItem.path : 'Finishing transfers...'
                });
            }

            if (currentItemIndex >= transferItems.length || overallTransferredBytes >= totalPlanBytes) {
                volumes.forEach(v => {
                    v.status = 'Complete';
                    v.speedBps = 0;
                    v.currentFile = 'Completed';
                    v.bytesTransferred = v.totalBytes;
                    v.filesTransferred = v.totalFiles;
                });
                if (onProgress) {
                    onProgress({
                        progressPercent: 100,
                        transferredBytes: totalPlanBytes,
                        totalBytes: totalPlanBytes,
                        filesTransferred: totalFiles,
                        totalFiles,
                        speedBps: 0,
                        volumes,
                        activeFile: 'All transfers finished'
                    });
                }
                if (onComplete) onComplete({ volumes, totalTransferred: totalPlanBytes });
                return;
            }

            timerId = setTimeout(tick, 100);
        }

        timerId = setTimeout(tick, 100);

        return {
            cancel() {
                isRunning = false;
                if (timerId) clearTimeout(timerId);
                volumes.forEach(v => {
                    if (v.status === 'Transferring') v.status = 'Idle';
                    v.speedBps = 0;
                });
            }
        };
    }
};
