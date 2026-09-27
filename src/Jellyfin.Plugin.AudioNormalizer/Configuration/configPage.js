/* Audio Normalizer settings page. Talks to the plugin's own endpoints under /AudioNormalizer. */
(function () {
    'use strict';

    var PLUGIN_ID = '2a968ad7-6168-44c8-b149-cf94eb870b25';
    var statusTimer = null;

    // ---------------------------------------------------------------- language

    var DICT = window.AudioNormalizerStrings || { en: {} };
    var LANG_KEY = 'audionormalizer.language';

    function detectLanguage() {
        // An explicit choice always wins. localStorage throws in some private-browsing modes,
        // so every access is guarded and the page simply falls back to detection.
        try {
            var saved = window.localStorage.getItem(LANG_KEY);
            if (saved && DICT[saved]) { return saved; }
        } catch (e) { /* storage unavailable */ }

        var candidates = [];
        if (window.Globalize && typeof window.Globalize.getCurrentLocale === 'function') {
            candidates.push(window.Globalize.getCurrentLocale());
        }
        candidates.push(document.documentElement.getAttribute('lang'));
        candidates.push(navigator.language);

        for (var i = 0; i < candidates.length; i++) {
            var c = candidates[i];
            if (!c) { continue; }
            var short = String(c).toLowerCase().split(/[-_]/)[0];
            if (DICT[short]) { return short; }
        }
        return 'en';
    }

    var lang = detectLanguage();

    /** Looks a string up in the active language, substituting {name} placeholders. */
    function t(key, vars) {
        var table = DICT[lang] || DICT.en || {};
        var s = table[key];
        if (s === undefined && DICT.en) { s = DICT.en[key]; }
        if (s === undefined) { return key; }
        if (vars) {
            Object.keys(vars).forEach(function (k) {
                s = s.split('{' + k + '}').join(vars[k]);
            });
        }
        return s;
    }

    /** Fills in every element in configPage.html that carries a data-an-i18n* attribute. */
    function applyStaticText(root) {
        root.querySelectorAll('[data-an-i18n]').forEach(function (el) {
            el.textContent = t(el.getAttribute('data-an-i18n'));
        });
        root.querySelectorAll('[data-an-i18n-html]').forEach(function (el) {
            // Only ever our own dictionary strings, which is why innerHTML is safe here.
            el.innerHTML = t(el.getAttribute('data-an-i18n-html'));
        });
        root.querySelectorAll('[data-an-i18n-title]').forEach(function (el) {
            el.title = t(el.getAttribute('data-an-i18n-title'));
        });
    }

    // ---------------------------------------------------------------- helpers

    function api(method, path, body) {
        var options = {
            type: method,
            url: ApiClient.getUrl('AudioNormalizer/' + path),
            contentType: 'application/json'
        };
        if (body !== undefined) {
            options.data = JSON.stringify(body);
        }
        if (method !== 'DELETE') {
            options.dataType = 'json';
        }
        return ApiClient.ajax(options);
    }

    function num(value, digits) {
        if (value === null || value === undefined) {
            return '—';
        }
        return Number(value).toFixed(digits === undefined ? 1 : digits);
    }

    // A quick scan samples the film, so its numbers are approximate. Marking them beats
    // showing an estimate that looks exactly like a full measurement.
    function numEst(value, isEstimate, digits) {
        var s = num(value, digits);
        return isEstimate && s !== '—' ? '~' + s : s;
    }

    function stateLabel(state) {
        var key = 'state.' + state;
        var text = t(key);
        return text === key ? t('state.Unknown') : text;
    }

    // ---------------------------------------------------------------- settings

    // Enum values travel as their NAMES, never as numbers. Jellyfin's JsonDefaults installs a
    // JsonStringEnumConverter, so this endpoint answers with "Dynaudnorm", not 0 - and the
    // option values in configPage.html are the enum names to match. With numbers the select
    // matched nothing after a reload: it silently showed no selection, and saving it back sent
    // NaN. Do not "tidy" these into integers.
    function loadConfig(page) {
        ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            var p = config.GlobalProfile || {};
            page.querySelector('#anTargetLufs').value = p.TargetLoudnessLufs;
            page.querySelector('#anMaxPeak').value = p.MaxTruePeakDb;
            page.querySelector('#anTargetLra').value = p.TargetDynamicRangeLu;
            page.querySelector('#anSkipBelow').value = p.SkipIfDynamicRangeBelowLu;
            page.querySelector('#anEngine').value = p.Engine;
            page.querySelector('#anAutoStrength').checked = !!p.AutoStrength;
            page.querySelector('#anStrength').value = p.Strength;
            page.querySelector('#anDownmix').value = p.Downmix;
            page.querySelector('#anCenter').value = p.CenterBoostDb;
            page.querySelector('#anSurround').value = p.SurroundGainDb;
            page.querySelector('#anLfe').value = p.LfeGainDb;
            page.querySelector('#anCodec').value = p.Codec;
            page.querySelector('#anContainer').value = p.Container;
            page.querySelector('#anBitrate').value = p.BitrateKbps;
            page.querySelector('#anPrefix').value = p.TrackTitlePrefix || 'AN';
            page.querySelector('#anAppendSource').checked = p.AppendSourceNameToTitle !== false;
            page.querySelector('#anTitle').value = p.TrackTitle || '';
            page.querySelector('#anMakeDefault').checked = !!p.MakeDefaultTrack;
            page.querySelector('#anDelay').value = p.ManualDelayMs;

            page.querySelector('#anParallel').value = config.MaxParallelJobs;
            page.querySelector('#anQuickScan').checked = !!config.QuickScan;
            page.querySelector('#anQuickWindows').value = config.QuickScanWindows;
            page.querySelector('#anQuickCoverage').value = config.QuickScanCoveragePercent;
            page.querySelector('#anSelectedOnly').checked = config.MeasureSelectedTracksOnly !== false;
            page.querySelector('#anPause').checked = !!config.PauseWhilePlaybackActive;
            page.querySelector('#anDryRun').checked = !!config.DryRun;
            page.querySelector('#anLogCommands').checked = !!config.LogFfmpegCommands;
            page.querySelector('#anMinFree').value = config.MinimumFreeSpaceGb;
            page.querySelector('#anMinDuration').value = config.MinimumDurationMinutes;
            page.querySelector('#anSourceSel').value = config.SourceSelection;
            page.querySelector('#anLangs').value = (config.PreferredSourceLanguages || []).join(', ');

            toggleStrengthRow(page);
        });
    }

    function toggleStrengthRow(page) {
        var auto = page.querySelector('#anAutoStrength').checked;
        page.querySelector('#anStrengthRow').style.display = auto ? 'none' : '';
    }

    function saveConfig(page) {
        return ApiClient.getPluginConfiguration(PLUGIN_ID).then(function (config) {
            var p = config.GlobalProfile || {};
            p.TargetLoudnessLufs = parseFloat(page.querySelector('#anTargetLufs').value);
            p.MaxTruePeakDb = parseFloat(page.querySelector('#anMaxPeak').value);
            p.TargetDynamicRangeLu = parseFloat(page.querySelector('#anTargetLra').value);
            p.SkipIfDynamicRangeBelowLu = parseFloat(page.querySelector('#anSkipBelow').value);
            p.Engine = page.querySelector('#anEngine').value;
            p.AutoStrength = page.querySelector('#anAutoStrength').checked;
            p.Strength = parseInt(page.querySelector('#anStrength').value, 10);
            p.Downmix = page.querySelector('#anDownmix').value;
            p.CenterBoostDb = parseFloat(page.querySelector('#anCenter').value);
            p.SurroundGainDb = parseFloat(page.querySelector('#anSurround').value);
            p.LfeGainDb = parseFloat(page.querySelector('#anLfe').value);
            p.Codec = page.querySelector('#anCodec').value;
            p.Container = page.querySelector('#anContainer').value;
            p.BitrateKbps = parseInt(page.querySelector('#anBitrate').value, 10);
            p.TrackTitlePrefix = page.querySelector('#anPrefix').value;
            p.AppendSourceNameToTitle = page.querySelector('#anAppendSource').checked;
            p.TrackTitle = page.querySelector('#anTitle').value;
            p.MakeDefaultTrack = page.querySelector('#anMakeDefault').checked;
            p.ManualDelayMs = parseInt(page.querySelector('#anDelay').value, 10) || 0;
            config.GlobalProfile = p;

            config.MaxParallelJobs = parseInt(page.querySelector('#anParallel').value, 10);
            config.QuickScan = page.querySelector('#anQuickScan').checked;
            config.QuickScanWindows = parseInt(page.querySelector('#anQuickWindows').value, 10);
            config.QuickScanCoveragePercent = parseInt(page.querySelector('#anQuickCoverage').value, 10);
            config.MeasureSelectedTracksOnly = page.querySelector('#anSelectedOnly').checked;
            config.PauseWhilePlaybackActive = page.querySelector('#anPause').checked;
            config.DryRun = page.querySelector('#anDryRun').checked;
            config.LogFfmpegCommands = page.querySelector('#anLogCommands').checked;
            config.MinimumFreeSpaceGb = parseFloat(page.querySelector('#anMinFree').value);
            config.MinimumDurationMinutes = parseInt(page.querySelector('#anMinDuration').value, 10);
            config.SourceSelection = page.querySelector('#anSourceSel').value;
            config.PreferredSourceLanguages = page.querySelector('#anLangs').value
                .split(',')
                .map(function (s) { return s.trim(); })
                .filter(function (s) { return s.length > 0; });

            return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
        }).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            loadDiagnostics(page);
        }, function (err) {
            // Without this a rejected save was completely silent: the page looked as if it had
            // saved, and the values were simply gone on the next load.
            Dashboard.alert(t('msg.saveFailed'));
            console.error('Audio Normalizer: saving the configuration failed', err);
        });
    }

    function loadDiagnostics(page) {
        api('GET', 'Diagnostics').then(function (d) {
            var html = '';
            html += '<div>ffmpeg: <code>' + (d.FfmpegPath || t('diag.unknownPath')) + '</code></div>';

            var missing = [];
            Object.keys(d.Filters || {}).forEach(function (f) {
                if (!d.Filters[f]) { missing.push(f); }
            });
            if (missing.length) {
                html += '<div style="color:#c33;">' + t('diag.missingFilters', { list: missing.join(', ') }) + '</div>';
            } else {
                html += '<div>' + t('diag.allFiltersPresent') + '</div>';
            }

            html += '<div>' + t('diag.candidates', { n: d.CandidateCount }) + '</div>';
            (d.Warnings || []).forEach(function (w) {
                html += '<div style="color:#c93;">⚠ ' + w + '</div>';
            });
            page.querySelector('#anDiagBody').innerHTML = html;
        }, function () {
            page.querySelector('#anDiagBody').textContent = t('diag.failed');
        });
    }

    // Built as DOM nodes rather than an HTML string. The rows carry a button now, and film
    // names come from library metadata, which has no business being parsed as markup.
    function queueRow(page, job, status) {
        var row = document.createElement('div');
        row.style.display = 'flex';
        row.style.alignItems = 'center';
        row.style.marginTop = '.25em';

        var name = document.createElement('span');
        name.style.marginRight = '.6em';
        name.textContent = job.ItemName + ' · ' + t('queue.kind.' + job.Kind) + ' · ' + status;
        row.appendChild(name);

        var drop = document.createElement('button');
        drop.className = 'raised';
        drop.textContent = t('btn.dequeue');
        drop.title = t('btn.dequeue.tip');
        drop.addEventListener('click', function () {
            api('POST', 'Dequeue', { ItemIds: [job.ItemId] }).then(function () {
                loadStatus(page);
                loadReport(page);
            });
        });
        row.appendChild(drop);

        return row;
    }

    function loadStatus(page) {
        api('GET', 'Status').then(function (s) {
            var host = page.querySelector('#anQueue');
            host.innerHTML = '';

            var line = document.createElement('div');
            line.textContent = t('queue.line', {
                pending: s.Pending,
                running: s.Running.length,
                done: s.Completed,
                failed: s.Failed
            });
            if (s.PausedForPlayback) {
                line.textContent += t('queue.pausedPlayback');
            }
            host.appendChild(line);

            (s.Running || []).forEach(function (j) {
                host.appendChild(queueRow(page, j, t('queue.runningPct', { percent: num(j.Progress, 0) })));
            });

            // The waiting list used to be a bare count, which is no help at all when the
            // question is "what is it still going to do, and can I take that one out".
            (s.PendingJobs || []).forEach(function (j) {
                host.appendChild(queueRow(page, j, t('queue.waiting')));
            });

            if (s.LastMessage) {
                var last = document.createElement('div');
                last.style.opacity = '.75';
                last.style.marginTop = '.4em';
                last.textContent = s.LastMessage;
                host.appendChild(last);
            }
        });
    }

    // ---------------------------------------------------------------- report

    var reportRows = [];
    var expanded = {};

    function saveTrackSelection(page, itemId, indexes, reset) {
        return api('POST', 'Tracks', {
            ItemId: itemId,
            StreamIndexes: indexes,
            ResetToAutomatic: !!reset
        }).then(function () {
            loadReport(page);
        });
    }
    var sortKey = 'SourceRangeLu';
    var sortDesc = true;

    // Rough throughput from measurement: analysis and encode together land around
    // 7x realtime on a modest CPU. Only ever used to set expectations.
    var REALTIME_FACTOR = 7;

    function renderSummary(page, rows) {
        var el = page.querySelector('#anSummary');
        var measured = rows.filter(function (r) { return r.SourceRangeLu !== null; });
        if (!measured.length) {
            el.textContent = '';
            return;
        }

        var target = parseFloat(page.querySelector('#anTargetLra').value);
        if (isNaN(target)) { target = 9; }

        var worth = measured.filter(function (r) { return r.SourceRangeLu > target; });
        var done = rows.filter(function (r) { return r.State === 'Done'; });
        var pending = worth.filter(function (r) { return r.State !== 'Done' && !r.Excluded; });

        var minutes = pending.reduce(function (a, r) { return a + (r.Minutes || 0); }, 0);
        var hours = minutes / 60 / REALTIME_FACTOR;
        var kbps = parseInt(page.querySelector('#anBitrate').value, 10) || 256;
        var gb = pending.reduce(function (a, r) { return a + (r.Minutes || 0) * 60 * kbps * 1000 / 8; }, 0)
                 / 1024 / 1024 / 1024;

        var worstList = measured.slice().sort(function (a, b) { return b.SourceRangeLu - a.SourceRangeLu; });
        var median = measured.slice().sort(function (a, b) { return a.SourceRangeLu - b.SourceRangeLu; })
                     [Math.floor(measured.length / 2)].SourceRangeLu;

        var html = '';
        html += '<div>' + t('summary.measured', {
            measured: measured.length,
            target: num(target),
            worth: worth.length,
            done: done.length
        }) + '</div>';
        html += '<div>' + t('summary.median', {
            median: num(median),
            worstName: worstList[0].Name,
            worstRange: num(worstList[0].SourceRangeLu)
        }) + '</div>';
        if (pending.length) {
            var time = hours < 1
                ? t('unit.minutes', { n: num(hours * 60, 0) })
                : t('unit.hours', { n: num(hours) });
            html += '<div>' + t('summary.remaining', {
                n: pending.length,
                time: time,
                gb: num(gb)
            }) + '</div>';
        } else {
            html += '<div>' + t('summary.nothing') + '</div>';
        }
        el.innerHTML = html;
    }

    function appendTrackRows(page, body, r) {
        if (!r.Tracks.length) {
            var none = document.createElement('tr');
            var td = document.createElement('td');
            td.colSpan = 13;
            td.style.padding = '4px 6px 10px 28px';
            td.style.opacity = '.7';
            td.textContent = t('track.notScanned');
            none.appendChild(td);
            body.appendChild(none);
            return;
        }

        r.Tracks.forEach(function (track) {
            var tr = document.createElement('tr');
            tr.style.background = 'rgba(128,128,128,.07)';

            function cell(text, align, indent) {
                var td = document.createElement('td');
                td.textContent = text;
                td.style.textAlign = align || 'left';
                td.style.padding = '3px 6px';
                td.style.fontSize = '.9em';
                if (indent) { td.style.paddingLeft = '28px'; }
                return td;
            }

            // Checkbox + track description in the name column.
            var first = document.createElement('td');
            first.style.padding = '3px 6px 3px 28px';
            first.style.fontSize = '.9em';

            var cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.checked = !!track.Selected;
            cb.style.marginRight = '8px';
            cb.title = t('track.normalizeThis');
            cb.addEventListener('change', function () {
                var indexes = r.Tracks
                    .filter(function (x) { return x.StreamIndex === track.StreamIndex ? cb.checked : x.Selected; })
                    .map(function (x) { return x.StreamIndex; });
                saveTrackSelection(page, r.ItemId, indexes, false);
            });
            first.appendChild(cb);

            var label = document.createElement('span');
            label.textContent = track.Label || ('stream ' + track.StreamIndex);
            first.appendChild(label);

            if (track.IsDefault) {
                var d = document.createElement('span');
                d.textContent = ' default';
                d.style.opacity = '.55';
                first.appendChild(d);
            }
            if (track.IsCommentary) {
                var c = document.createElement('span');
                c.textContent = t('track.commentary');
                c.style.opacity = '.55';
                first.appendChild(c);
            }
            tr.appendChild(first);

            tr.appendChild(cell('', 'right'));
            tr.appendChild(cell('', 'center'));
            tr.appendChild(cell(track.Selected ? track.PlannedTitle : '—'));
            tr.appendChild(cell(numEst(track.SourceLufs, track.SourceIsEstimate), 'right'));

            var rangeCell = cell(numEst(track.SourceRangeLu, track.SourceIsEstimate), 'right');
            if (track.SourceRangeLu !== null && track.SourceRangeLu >= 18) {
                rangeCell.style.color = '#e06c3b';
            }
            tr.appendChild(rangeCell);

            tr.appendChild(cell(numEst(track.SourceLowLufs, track.SourceIsEstimate), 'right'));
            tr.appendChild(cell(numEst(track.SourceHighLufs, track.SourceIsEstimate), 'right'));
            tr.appendChild(cell(numEst(track.SourcePeakDb, track.SourceIsEstimate), 'right'));
            tr.appendChild(cell(track.ResultRangeLu === null ? '—' : num(track.ResultRangeLu), 'right'));
            tr.appendChild(cell(track.Selected ? stateLabel(track.State) : '—'));
            tr.appendChild(cell(track.OutputMb ? num(track.OutputMb) : '—', 'right'));

            var actions = document.createElement('td');
            actions.style.textAlign = 'right';
            actions.style.whiteSpace = 'nowrap';
            actions.style.padding = '3px 6px';

            if (track.OutputPath) {
                var del = document.createElement('button');
                del.className = 'raised';
                del.style.marginLeft = '4px';
                del.textContent = t('btn.delete');
                del.addEventListener('click', function () {
                    api('DELETE', 'Track/' + r.ItemId + '?streamIndex=' + track.StreamIndex)
                        .then(function () { loadReport(page); });
                });
                actions.appendChild(del);
            }

            tr.appendChild(actions);

            if (track.Note) { tr.title = track.Note; }
            body.appendChild(tr);
        });

        // A footer row for resetting back to the automatic choice.
        var footer = document.createElement('tr');
        footer.style.background = 'rgba(128,128,128,.07)';
        var ftd = document.createElement('td');
        ftd.colSpan = 13;
        ftd.style.padding = '2px 6px 10px 28px';
        ftd.style.fontSize = '.85em';

        var explicit = r.Tracks.some(function (x) { return x.SelectionIsExplicit; });
        var note = document.createElement('span');
        note.style.opacity = '.7';
        note.textContent = explicit ? t('track.manualChoice') : t('track.autoChoice');
        ftd.appendChild(note);

        if (explicit) {
            var reset = document.createElement('a');
            reset.href = '#';
            reset.textContent = t('track.resetAuto');
            reset.addEventListener('click', function (e) {
                e.preventDefault();
                saveTrackSelection(page, r.ItemId, [], true);
            });
            ftd.appendChild(reset);
        }

        footer.appendChild(ftd);
        body.appendChild(footer);
    }

    function sortRows(rows) {
        return rows.slice().sort(function (a, b) {
            var x = a[sortKey], y = b[sortKey];
            if (x === null || x === undefined) { return 1; }
            if (y === null || y === undefined) { return -1; }
            if (typeof x === 'string') {
                return sortDesc ? y.localeCompare(x) : x.localeCompare(y);
            }
            return sortDesc ? y - x : x - y;
        });
    }

    function loadReport(page) {
        var only = page.querySelector('#anOnlyProblems').checked;
        api('GET', 'Report?onlyProblems=' + (only ? 'true' : 'false')).then(function (rows) {
            var body = page.querySelector('#anReportBody');
            body.innerHTML = '';

            if (!rows.length) {
                // The table now lists every candidate film, scanned or not, so an empty one
                // means either the libraries hold nothing or the filter is hiding it all.
                page.querySelector('#anReportEmpty').textContent =
                    only ? t('report.emptyFiltered') : t('report.empty');
                return;
            }
            page.querySelector('#anReportEmpty').textContent = '';
            reportRows = rows;
            renderSummary(page, rows);

            sortRows(rows).forEach(function (r) {
                var tr = document.createElement('tr');
                tr.style.borderBottom = '1px solid rgba(128,128,128,.25)';

                function cell(text, align) {
                    var td = document.createElement('td');
                    td.textContent = text;
                    td.style.textAlign = align || 'left';
                    td.style.padding = '4px 6px';
                    return td;
                }

                var nameCell = cell((expanded[r.ItemId] ? '▾  ' : '▸  ') + r.Name);
                nameCell.style.cursor = 'pointer';
                nameCell.title = t('report.showTracks');
                nameCell.addEventListener('click', function () {
                    expanded[r.ItemId] = !expanded[r.ItemId];
                    loadReport(page);
                });
                tr.appendChild(nameCell);

                tr.appendChild(cell(num(r.Minutes, 0), 'right'));

                var countCell = cell(
                    r.SelectedCount + (r.Tracks.length ? ' / ' + r.Tracks.length : ''), 'center');
                if (r.Tracks.length && r.SelectedCount === 0) {
                    countCell.style.opacity = '.5';
                    countCell.title = t('report.noTracksSelected');
                }
                tr.appendChild(countCell);

                tr.appendChild(cell(r.SourceTrack || '—'));
                tr.appendChild(cell(numEst(r.SourceLufs, r.SourceIsEstimate), 'right'));

                var rangeCell = cell(numEst(r.SourceRangeLu, r.SourceIsEstimate), 'right');
                if (r.SourceRangeLu !== null && r.SourceRangeLu >= 18) {
                    rangeCell.style.color = '#e06c3b';
                    rangeCell.style.fontWeight = '600';
                    rangeCell.title = t('report.rangeHigh');
                }
                tr.appendChild(rangeCell);

                // The two ends of the range: roughly where the dialogue sits and where the
                // loud scenes sit. This is the min/max the range is measured between.
                tr.appendChild(cell(numEst(r.SourceLowLufs, r.SourceIsEstimate), 'right'));
                tr.appendChild(cell(numEst(r.SourceHighLufs, r.SourceIsEstimate), 'right'));

                tr.appendChild(cell(numEst(r.SourcePeakDb, r.SourceIsEstimate), 'right'));
                tr.appendChild(cell(r.ResultRangeLu === null ? '—' : num(r.ResultRangeLu), 'right'));
                tr.appendChild(cell(stateLabel(r.State) + (r.Excluded ? t('report.excluded') : '')));
                tr.appendChild(cell(r.OutputMb ? num(r.OutputMb) : '—', 'right'));

                var actions = document.createElement('td');
                actions.style.textAlign = 'right';
                actions.style.whiteSpace = 'nowrap';

                // Measuring one film. The tracks and the name are listed before any scan, so
                // this is how a single title gets its numbers without running the whole library.
                var measure = document.createElement('button');
                measure.className = 'raised';
                measure.style.marginLeft = '4px';
                measure.textContent = t('btn.measure');
                measure.title = t('btn.measure.tip');
                measure.addEventListener('click', function () {
                    api('POST', 'Analyze', { ItemIds: [r.ItemId] }).then(function () {
                        loadStatus(page);
                    });
                });
                actions.appendChild(measure);

                var build = document.createElement('button');
                build.className = 'raised';
                build.style.marginLeft = '4px';
                build.textContent = r.OutputPath ? t('btn.rebuild') : t('btn.build');
                build.addEventListener('click', function () {
                    api('POST', 'Generate', { ItemIds: [r.ItemId], Force: true }).then(function () {
                        loadStatus(page);
                    });
                });
                actions.appendChild(build);

                if (r.OutputPath) {
                    var del = document.createElement('button');
                    del.className = 'raised';
                    del.style.marginLeft = '4px';
                    del.textContent = t('btn.delete');
                    del.addEventListener('click', function () {
                        Dashboard.confirm(t('confirm.deleteTrack'), 'Audio Normalizer', function (ok) {
                            if (!ok) { return; }
                            api('DELETE', 'Track/' + r.ItemId).then(function () {
                                loadReport(page);
                            });
                        });
                    });
                    actions.appendChild(del);
                }

                var prof = document.createElement('button');
                prof.className = 'raised';
                prof.style.marginLeft = '4px';
                prof.textContent = r.HasOverride ? t('btn.settingsStar') : t('btn.settings');
                if (r.HasOverride) { prof.title = t('report.hasOverride'); }
                prof.addEventListener('click', function () {
                    openOverride(page, r);
                });
                actions.appendChild(prof);

                var diag = document.createElement('button');
                diag.className = 'raised';
                diag.style.marginLeft = '4px';
                diag.textContent = t('btn.diagnostics');
                diag.addEventListener('click', function () {
                    runSelfTest(page, r.ItemId);
                });
                actions.appendChild(diag);

                var excl = document.createElement('button');
                excl.className = 'raised';
                excl.style.marginLeft = '4px';
                excl.textContent = r.Excluded ? t('btn.include') : t('btn.exclude');
                excl.addEventListener('click', function () {
                    api('POST', 'Override', { ItemId: r.ItemId, Excluded: !r.Excluded }).then(function () {
                        loadReport(page);
                    });
                });
                actions.appendChild(excl);

                tr.appendChild(actions);

                if (r.Note) {
                    tr.title = r.Note;
                }

                body.appendChild(tr);

                if (expanded[r.ItemId]) {
                    appendTrackRows(page, body, r);
                }
            });
        });
    }

    // ---------------------------------------------------------------- per-item overrides

    // The fields mirror the global ones; anything not listed here simply inherits whatever the
    // global profile has at generation time. Labels and option texts are dictionary KEYS,
    // resolved when the editor is built, so switching language re-renders them correctly.
    // Select values are enum NAMES, matching what the API returns - see loadConfig.
    // literalOptions marks a select whose option texts are format names, identical in every
    // language and therefore not translated.
    var OVERRIDE_FIELDS = [
        { key: 'TargetLoudnessLufs',       labelKey: 'ov.targetLufs',   type: 'number', step: '0.5' },
        { key: 'MaxTruePeakDb',            labelKey: 'ov.maxPeak',      type: 'number', step: '0.1' },
        { key: 'TargetDynamicRangeLu',     labelKey: 'ov.targetLra',    type: 'number', step: '0.5' },
        { key: 'SkipIfDynamicRangeBelowLu',labelKey: 'ov.skipBelow',    type: 'number', step: '0.5' },
        { key: 'Engine',                   labelKey: 'ov.engine',       type: 'select',
          options: [['Dynaudnorm', 'short.engine.Dynaudnorm'],
                    ['SpeechNorm', 'short.engine.SpeechNorm'],
                    ['Compressor', 'short.engine.Compressor'],
                    ['LoudnessOnly', 'short.engine.LoudnessOnly']] },
        { key: 'AutoStrength',             labelKey: 'ov.autoStrength', type: 'check' },
        { key: 'Strength',                 labelKey: 'ov.strength',     type: 'number', step: '5' },
        { key: 'Downmix',                  labelKey: 'ov.downmix',      type: 'select',
          options: [['DialogueStereo', 'short.downmix.DialogueStereo'],
                    ['PlainStereo', 'short.downmix.PlainStereo'],
                    ['KeepLayout', 'short.downmix.KeepLayout']] },
        { key: 'CenterBoostDb',            labelKey: 'ov.center',       type: 'number', step: '0.5' },
        { key: 'SurroundGainDb',           labelKey: 'ov.surround',     type: 'number', step: '0.5' },
        { key: 'LfeGainDb',                labelKey: 'ov.lfe',          type: 'number', step: '1' },
        { key: 'Codec',                    labelKey: 'ov.codec',        type: 'select', literalOptions: true,
          options: [['Aac', 'AAC'], ['Eac3', 'E-AC3'], ['Ac3', 'AC3'], ['Flac', 'FLAC'], ['Opus', 'Opus']] },
        { key: 'BitrateKbps',              labelKey: 'ov.bitrate',      type: 'number', step: '32' },
        { key: 'ManualDelayMs',            labelKey: 'ov.delay',        type: 'number', step: '5' }
    ];

    var overrideItemId = null;

    function openOverride(page, row) {
        overrideItemId = row.ItemId;
        var section = page.querySelector('#anOverrideSection');
        page.querySelector('#anOverrideName').textContent = row.Name;
        section.style.display = '';
        section.scrollIntoView({ behavior: 'smooth', block: 'start' });

        var host = page.querySelector('#anOverrideFields');
        host.textContent = t('ov.loading');

        api('GET', 'Override/' + row.ItemId).then(function (profile) {
            host.innerHTML = '';
            OVERRIDE_FIELDS.forEach(function (f) {
                var wrap = document.createElement('div');
                wrap.style.marginBottom = '0.5em';

                if (f.type === 'check') {
                    var lab = document.createElement('label');
                    lab.className = 'checkboxContainer';
                    var cb = document.createElement('input');
                    cb.type = 'checkbox';
                    cb.id = 'ov_' + f.key;
                    cb.checked = !!profile[f.key];
                    var sp = document.createElement('span');
                    sp.textContent = t(f.labelKey);
                    lab.appendChild(cb);
                    lab.appendChild(sp);
                    wrap.appendChild(lab);
                } else {
                    var lbl = document.createElement('label');
                    lbl.textContent = t(f.labelKey);
                    lbl.style.display = 'block';
                    lbl.style.fontSize = '.85em';
                    lbl.style.opacity = '.8';
                    lbl.htmlFor = 'ov_' + f.key;
                    wrap.appendChild(lbl);

                    var input;
                    if (f.type === 'select') {
                        input = document.createElement('select');
                        f.options.forEach(function (o) {
                            var opt = document.createElement('option');
                            opt.value = o[0];
                            opt.textContent = f.literalOptions ? o[1] : t(o[1]);
                            input.appendChild(opt);
                        });
                        input.value = String(profile[f.key]);
                    } else {
                        input = document.createElement('input');
                        input.type = 'number';
                        input.step = f.step || '1';
                        input.value = profile[f.key];
                    }
                    input.id = 'ov_' + f.key;
                    input.style.width = '16em';
                    wrap.appendChild(input);
                }

                host.appendChild(wrap);
            });
        }, function () {
            host.textContent = t('ov.loadFailed');
        });
    }

    function saveOverride(page) {
        if (!overrideItemId) { return; }
        var profile = {};
        OVERRIDE_FIELDS.forEach(function (f) {
            var el = page.querySelector('#ov_' + f.key);
            if (!el) { return; }
            if (f.type === 'check') {
                profile[f.key] = el.checked;
            } else if (f.type === 'select') {
                profile[f.key] = el.value;
            } else {
                profile[f.key] = parseFloat(el.value);
            }
        });
        // The title is not exposed per item on purpose: changing it per film would scatter
        // differently named tracks across the library.
        api('POST', 'Override', { ItemId: overrideItemId, Excluded: false, Profile: profile })
            .then(function () {
                Dashboard.alert(t('ov.saved'));
                loadReport(page);
            }, function (err) {
                Dashboard.alert(t('ov.saveFailed'));
                console.error('Audio Normalizer: saving the per-item override failed', err);
            });
    }

    function resetOverride(page) {
        if (!overrideItemId) { return; }
        api('POST', 'Override', { ItemId: overrideItemId, Excluded: false })
            .then(function () {
                page.querySelector('#anOverrideSection').style.display = 'none';
                overrideItemId = null;
                loadReport(page);
            });
    }

    // ---------------------------------------------------------------- self-test

    function runSelfTest(page, itemId) {
        var section = page.querySelector('#anSelfTestSection');
        var out = page.querySelector('#anSelfTestOut');
        section.style.display = '';
        out.textContent = t('st.checking');
        section.scrollIntoView({ behavior: 'smooth', block: 'start' });

        // Padded here rather than in the dictionary: label lengths differ per language, so the
        // column has to be lined up at render time to stay readable.
        function row(labelKey, value) {
            var s = t(labelKey) + ':';
            while (s.length < 20) { s += ' '; }
            return s + value;
        }

        api('GET', 'SelfTest/' + itemId + '?run=true').then(function (d) {
            var L = [];
            L.push(row('st.film', d.ItemName || '—'));
            L.push(row('st.file', d.SourcePath || '—'));
            if (d.Refusal) {
                L.push('');
                L.push(t('st.refusal', { reason: d.Refusal }));
                out.textContent = L.join('\n');
                return;
            }
            L.push('');
            L.push(t('st.sourceTracks'));
            (d.AudioStreams || []).forEach(function (s) { L.push('  ' + s); });
            L.push(row('st.chosen', d.ChosenStream));
            L.push('');
            L.push(row('st.outputPath', d.OutputPath));
            L.push(row('st.folderWritable', d.FolderWritable ? t('st.yes') : t('st.no')));
            L.push(row('st.freeSpace', d.FreeSpaceGb + ' GB'));
            L.push(row('st.ffmpeg', d.FfmpegPath));
            L.push(row('st.strength', d.ResolvedMaxGain));
            if (d.Measured) {
                L.push('');
                L.push(t('st.measured', {
                    lufs: num(d.Measured.IntegratedLufs),
                    range: num(d.Measured.LoudnessRangeLu),
                    peak: num(d.Measured.TruePeakDb)
                }));
            }
            if ((d.Problems || []).length) {
                L.push('');
                L.push(t('st.problems'));
                d.Problems.forEach(function (p) { L.push('  ! ' + p); });
            }
            L.push('');
            L.push(t('st.analysisCommand'));
            L.push(d.AnalysisCommand);
            L.push('');
            L.push(t('st.encodeCommand'));
            L.push(d.EncodeCommand);
            if (d.FfmpegOutput) {
                L.push('');
                L.push(t('st.ffmpegOutput'));
                L.push(d.FfmpegOutput);
            }
            out.textContent = L.join('\n');
        }, function (e) {
            out.textContent = t('st.requestFailed', { error: (e && e.statusText ? e.statusText : e) });
        });
    }

    // ---------------------------------------------------------------- wiring

    document.querySelector('#AudioNormalizerConfigPage').addEventListener('pageshow', function () {
        var page = this;
        page.querySelector('#anLanguage').value = lang;
        applyStaticText(page);

        loadConfig(page);
        loadDiagnostics(page);
        loadStatus(page);
        loadReport(page);

        if (statusTimer) { clearInterval(statusTimer); }
        statusTimer = setInterval(function () { loadStatus(page); }, 4000);
    });

    document.querySelector('#AudioNormalizerConfigPage').addEventListener('pagehide', function () {
        if (statusTimer) {
            clearInterval(statusTimer);
            statusTimer = null;
        }
    });

    document.querySelector('#anLanguage').addEventListener('change', function () {
        lang = this.value;
        try {
            window.localStorage.setItem(LANG_KEY, lang);
        } catch (e) { /* storage unavailable, the choice just will not be remembered */ }

        var page = document.querySelector('#AudioNormalizerConfigPage');
        applyStaticText(page);
        // Everything else on the page is rendered from data, so it has to be rebuilt.
        loadDiagnostics(page);
        loadStatus(page);
        loadReport(page);
    });

    document.querySelector('#AudioNormalizerConfigPage').addEventListener('click', function (e) {
        var page = this;
        var id = e.target && e.target.closest ? (e.target.closest('button') || {}).id : null;

        if (id === 'anBtnAnalyzeAll') {
            api('POST', 'Analyze', { ItemIds: [] }).then(function (n) {
                Dashboard.alert(t('msg.queuedAnalyze', { n: n }));
                loadStatus(page);
            });
        } else if (id === 'anBtnGenerateAll') {
            api('POST', 'Generate', { ItemIds: [], Force: false }).then(function (n) {
                Dashboard.alert(t('msg.queuedGenerate', { n: n }));
                loadStatus(page);
            });
        } else if (id === 'anBtnCancel') {
            api('POST', 'Cancel').then(function (n) {
                Dashboard.alert(t('msg.dequeued', { n: n }));
                loadStatus(page);
            });
        }
    });

    document.querySelector('#anAutoStrength').addEventListener('change', function () {
        toggleStrengthRow(document.querySelector('#AudioNormalizerConfigPage'));
    });

    document.querySelector('#anOverrideSave').addEventListener('click', function () {
        saveOverride(document.querySelector('#AudioNormalizerConfigPage'));
    });
    document.querySelector('#anOverrideReset').addEventListener('click', function () {
        resetOverride(document.querySelector('#AudioNormalizerConfigPage'));
    });
    document.querySelector('#anOverrideClose').addEventListener('click', function () {
        document.querySelector('#anOverrideSection').style.display = 'none';
        overrideItemId = null;
    });

    document.querySelector('#anReportHead').addEventListener('click', function (e) {
        var th = e.target.closest('th');
        if (!th || !th.dataset.sort) { return; }
        if (sortKey === th.dataset.sort) {
            sortDesc = !sortDesc;
        } else {
            sortKey = th.dataset.sort;
            sortDesc = true;
        }
        loadReport(document.querySelector('#AudioNormalizerConfigPage'));
    });

    document.querySelector('#anOnlyProblems').addEventListener('change', function () {
        loadReport(document.querySelector('#AudioNormalizerConfigPage'));
    });

    document.querySelector('#anConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        saveConfig(document.querySelector('#AudioNormalizerConfigPage')).then(function () {
            Dashboard.hideLoadingMsg();
        }, function () {
            Dashboard.hideLoadingMsg();
        });
        return false;
    });
})();
