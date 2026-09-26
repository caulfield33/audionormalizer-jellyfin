/* Audio Normalizer settings page. Talks to the plugin's own endpoints under /AudioNormalizer. */
(function () {
    'use strict';

    var PLUGIN_ID = '2a968ad7-6168-44c8-b149-cf94eb870b25';
    var statusTimer = null;

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

    function stateLabel(state) {
        switch (state) {
            case 'Analyzed': return 'виміряно';
            case 'Queued': return 'у черзі';
            case 'Running': return 'обробляється';
            case 'Done': return 'готово';
            case 'Skipped': return 'пропущено';
            case 'Failed': return 'помилка';
            case 'Stale': return 'застаріло';
            default: return 'невідомо';
        }
    }

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
            p.Engine = parseInt(page.querySelector('#anEngine').value, 10);
            p.AutoStrength = page.querySelector('#anAutoStrength').checked;
            p.Strength = parseInt(page.querySelector('#anStrength').value, 10);
            p.Downmix = parseInt(page.querySelector('#anDownmix').value, 10);
            p.CenterBoostDb = parseFloat(page.querySelector('#anCenter').value);
            p.SurroundGainDb = parseFloat(page.querySelector('#anSurround').value);
            p.LfeGainDb = parseFloat(page.querySelector('#anLfe').value);
            p.Codec = parseInt(page.querySelector('#anCodec').value, 10);
            p.Container = parseInt(page.querySelector('#anContainer').value, 10);
            p.BitrateKbps = parseInt(page.querySelector('#anBitrate').value, 10);
            p.TrackTitlePrefix = page.querySelector('#anPrefix').value;
            p.AppendSourceNameToTitle = page.querySelector('#anAppendSource').checked;
            p.TrackTitle = page.querySelector('#anTitle').value;
            p.MakeDefaultTrack = page.querySelector('#anMakeDefault').checked;
            p.ManualDelayMs = parseInt(page.querySelector('#anDelay').value, 10) || 0;
            config.GlobalProfile = p;

            config.MaxParallelJobs = parseInt(page.querySelector('#anParallel').value, 10);
            config.PauseWhilePlaybackActive = page.querySelector('#anPause').checked;
            config.DryRun = page.querySelector('#anDryRun').checked;
            config.LogFfmpegCommands = page.querySelector('#anLogCommands').checked;
            config.MinimumFreeSpaceGb = parseFloat(page.querySelector('#anMinFree').value);
            config.MinimumDurationMinutes = parseInt(page.querySelector('#anMinDuration').value, 10);
            config.SourceSelection = parseInt(page.querySelector('#anSourceSel').value, 10);
            config.PreferredSourceLanguages = page.querySelector('#anLangs').value
                .split(',')
                .map(function (s) { return s.trim(); })
                .filter(function (s) { return s.length > 0; });

            return ApiClient.updatePluginConfiguration(PLUGIN_ID, config);
        }).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            loadDiagnostics(page);
        });
    }

    function loadDiagnostics(page) {
        api('GET', 'Diagnostics').then(function (d) {
            var html = '';
            html += '<div>ffmpeg: <code>' + (d.FfmpegPath || 'невідомо') + '</code></div>';

            var missing = [];
            Object.keys(d.Filters || {}).forEach(function (f) {
                if (!d.Filters[f]) { missing.push(f); }
            });
            if (missing.length) {
                html += '<div style="color:#c33;">Немає фільтрів: ' + missing.join(', ') + '</div>';
            } else {
                html += '<div>Усі потрібні фільтри на місці.</div>';
            }

            html += '<div>Придатних до обробки одиниць медіа: ' + d.CandidateCount + '</div>';
            (d.Warnings || []).forEach(function (w) {
                html += '<div style="color:#c93;">⚠ ' + w + '</div>';
            });
            page.querySelector('#anDiagBody').innerHTML = html;
        }, function () {
            page.querySelector('#anDiagBody').textContent = 'Не вдалось отримати стан.';
        });
    }

    function loadStatus(page) {
        api('GET', 'Status').then(function (s) {
            var text = 'У черзі: ' + s.Pending + ' · виконується: ' + s.Running.length +
                ' · завершено: ' + s.Completed + ' · помилок: ' + s.Failed;
            if (s.PausedForPlayback) {
                text += ' · призупинено, бо йде відтворення';
            }
            if (s.Running.length) {
                text += '<br/>' + s.Running.map(function (j) {
                    return j.ItemName + ' — ' + num(j.Progress, 0) + '%';
                }).join('<br/>');
            }
            if (s.LastMessage) {
                text += '<br/><span style="opacity:.75;">' + s.LastMessage + '</span>';
            }
            page.querySelector('#anQueue').innerHTML = text;
        });
    }

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
        html += '<div>Виміряно: <b>' + measured.length + '</b>' +
                ' · потребують обробки (різниця більша за ' + num(target) + ' LU): <b>' + worth.length + '</b>' +
                ' · уже готово: <b>' + done.length + '</b></div>';
        html += '<div>Медіанна різниця по бібліотеці: <b>' + num(median) + ' LU</b>' +
                ' · найгірший: ' + worstList[0].Name + ' (' + num(worstList[0].SourceRangeLu) + ' LU)</div>';
        if (pending.length) {
            html += '<div>Лишилось обробити ' + pending.length + ' — це приблизно <b>' +
                    (hours < 1 ? num(hours * 60, 0) + ' хв' : num(hours) + ' год') +
                    '</b> роботи і <b>' + num(gb) + ' ГБ</b> на диску.</div>';
        } else {
            html += '<div>Немає нічого, що потребувало б обробки за поточними налаштуваннями.</div>';
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
            td.textContent = 'Аудіодоріжки ще не проскановано. Запустіть «Виміряти всю бібліотеку».';
            none.appendChild(td);
            body.appendChild(none);
            return;
        }

        r.Tracks.forEach(function (t) {
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
            cb.checked = !!t.Selected;
            cb.style.marginRight = '8px';
            cb.title = 'Нормалізувати цю доріжку';
            cb.addEventListener('change', function () {
                var indexes = r.Tracks
                    .filter(function (x) { return x.StreamIndex === t.StreamIndex ? cb.checked : x.Selected; })
                    .map(function (x) { return x.StreamIndex; });
                saveTrackSelection(page, r.ItemId, indexes, false);
            });
            first.appendChild(cb);

            var label = document.createElement('span');
            label.textContent = t.Label || ('stream ' + t.StreamIndex);
            first.appendChild(label);

            if (t.IsDefault) {
                var d = document.createElement('span');
                d.textContent = ' default';
                d.style.opacity = '.55';
                first.appendChild(d);
            }
            if (t.IsCommentary) {
                var c = document.createElement('span');
                c.textContent = ' коментар';
                c.style.opacity = '.55';
                first.appendChild(c);
            }
            tr.appendChild(first);

            tr.appendChild(cell('', 'right'));
            tr.appendChild(cell('', 'center'));
            tr.appendChild(cell(t.Selected ? t.PlannedTitle : '—'));
            tr.appendChild(cell(num(t.SourceLufs), 'right'));

            var rangeCell = cell(num(t.SourceRangeLu), 'right');
            if (t.SourceRangeLu !== null && t.SourceRangeLu >= 18) {
                rangeCell.style.color = '#e06c3b';
            }
            tr.appendChild(rangeCell);

            tr.appendChild(cell(num(t.SourceLowLufs), 'right'));
            tr.appendChild(cell(num(t.SourceHighLufs), 'right'));
            tr.appendChild(cell(num(t.SourcePeakDb), 'right'));
            tr.appendChild(cell(t.ResultRangeLu === null ? '—' : num(t.ResultRangeLu), 'right'));
            tr.appendChild(cell(t.Selected ? stateLabel(t.State) : '—'));
            tr.appendChild(cell(t.OutputMb ? num(t.OutputMb) : '—', 'right'));

            var actions = document.createElement('td');
            actions.style.textAlign = 'right';
            actions.style.whiteSpace = 'nowrap';
            actions.style.padding = '3px 6px';

            if (t.OutputPath) {
                var del = document.createElement('button');
                del.className = 'raised';
                del.style.marginLeft = '4px';
                del.textContent = 'Видалити';
                del.addEventListener('click', function () {
                    api('DELETE', 'Track/' + r.ItemId + '?streamIndex=' + t.StreamIndex)
                        .then(function () { loadReport(page); });
                });
                actions.appendChild(del);
            }

            tr.appendChild(actions);

            if (t.Note) { tr.title = t.Note; }
            body.appendChild(tr);
        });

        // A footer row for resetting back to the automatic choice.
        var footer = document.createElement('tr');
        footer.style.background = 'rgba(128,128,128,.07)';
        var ftd = document.createElement('td');
        ftd.colSpan = 13;
        ftd.style.padding = '2px 6px 10px 28px';
        ftd.style.fontSize = '.85em';

        var explicit = r.Tracks.some(function (t) { return t.SelectionIsExplicit; });
        var note = document.createElement('span');
        note.style.opacity = '.7';
        note.textContent = explicit
            ? 'Доріжки обрано вручну. '
            : 'Доріжку обрано автоматично за правилом із налаштувань. ';
        ftd.appendChild(note);

        if (explicit) {
            var reset = document.createElement('a');
            reset.href = '#';
            reset.textContent = 'Повернути автоматичний вибір';
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
                page.querySelector('#anReportEmpty').textContent =
                    'Поки порожньо. Запустіть «Виміряти всю бібліотеку» — це нічого не змінює на диску.';
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

                var nameCell = cell((expanded[r.ItemId] ? '\u25BE  ' : '\u25B8  ') + r.Name);
                nameCell.style.cursor = 'pointer';
                nameCell.title = 'Показати аудіодоріжки';
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
                    countCell.title = 'Жодної доріжки не обрано — нічого не буде створено';
                }
                tr.appendChild(countCell);

                tr.appendChild(cell(r.SourceTrack || '—'));
                tr.appendChild(cell(num(r.SourceLufs), 'right'));

                var rangeCell = cell(num(r.SourceRangeLu), 'right');
                if (r.SourceRangeLu !== null && r.SourceRangeLu >= 18) {
                    rangeCell.style.color = '#e06c3b';
                    rangeCell.style.fontWeight = '600';
                    rangeCell.title = 'Велика різниця між тихим і гучним — цей фільм вартий обробки';
                }
                tr.appendChild(rangeCell);

                // The two ends of the range: roughly where the dialogue sits and where the
                // loud scenes sit. This is the min/max the range is measured between.
                tr.appendChild(cell(num(r.SourceLowLufs), 'right'));
                tr.appendChild(cell(num(r.SourceHighLufs), 'right'));

                tr.appendChild(cell(num(r.SourcePeakDb), 'right'));
                tr.appendChild(cell(r.ResultRangeLu === null ? '—' : num(r.ResultRangeLu), 'right'));
                tr.appendChild(cell(stateLabel(r.State) + (r.Excluded ? ' (виключено)' : '')));
                tr.appendChild(cell(r.OutputMb ? num(r.OutputMb) : '—', 'right'));

                var actions = document.createElement('td');
                actions.style.textAlign = 'right';
                actions.style.whiteSpace = 'nowrap';

                var build = document.createElement('button');
                build.className = 'raised';
                build.style.marginLeft = '4px';
                build.textContent = r.OutputPath ? 'Перебудувати' : 'Створити';
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
                    del.textContent = 'Видалити';
                    del.addEventListener('click', function () {
                        Dashboard.confirm('Видалити нормалізовану доріжку? Оригінал не постраждає.', 'Audio Normalizer', function (ok) {
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
                prof.textContent = r.HasOverride ? 'Налаштування *' : 'Налаштування';
                if (r.HasOverride) { prof.title = 'Цей фільм має власні налаштування'; }
                prof.addEventListener('click', function () {
                    openOverride(page, r);
                });
                actions.appendChild(prof);

                var diag = document.createElement('button');
                diag.className = 'raised';
                diag.style.marginLeft = '4px';
                diag.textContent = 'Діагностика';
                diag.addEventListener('click', function () {
                    runSelfTest(page, r.ItemId);
                });
                actions.appendChild(diag);

                var excl = document.createElement('button');
                excl.className = 'raised';
                excl.style.marginLeft = '4px';
                excl.textContent = r.Excluded ? 'Повернути' : 'Виключити';
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

    // Per-item overrides. The fields mirror the global ones; anything not listed here
    // simply inherits whatever the global profile has at generation time.
    var OVERRIDE_FIELDS = [
        { key: 'TargetLoudnessLufs',       label: 'Цільова гучність (LUFS)',        type: 'number', step: '0.5' },
        { key: 'MaxTruePeakDb',            label: 'Максимальний пік (dBTP)',        type: 'number', step: '0.1' },
        { key: 'TargetDynamicRangeLu',     label: 'Допустима різниця (LU)',         type: 'number', step: '0.5' },
        { key: 'SkipIfDynamicRangeBelowLu',label: 'Не обробляти якщо менше (LU)',   type: 'number', step: '0.5' },
        { key: 'Engine',                   label: 'Алгоритм',                       type: 'select',
          options: [['0','dynaudnorm'],['1','speechnorm'],['2','Компресор'],['3','Лише гучність']] },
        { key: 'AutoStrength',             label: 'Підбирати силу автоматично',     type: 'check' },
        { key: 'Strength',                 label: 'Сила, 0–100',                    type: 'number', step: '5' },
        { key: 'Downmix',                  label: 'Розкладка',                      type: 'select',
          options: [['0','Стерео з акцентом на діалог'],['1','Звичайне стерео'],['2','Як в оригіналі']] },
        { key: 'CenterBoostDb',            label: 'Центр (діалоги), дБ',            type: 'number', step: '0.5' },
        { key: 'SurroundGainDb',           label: 'Тили, дБ',                       type: 'number', step: '0.5' },
        { key: 'LfeGainDb',                label: 'Сабвуфер LFE, дБ',               type: 'number', step: '1' },
        { key: 'Codec',                    label: 'Кодек',                          type: 'select',
          options: [['0','AAC'],['1','E-AC3'],['2','AC3'],['3','FLAC'],['4','Opus']] },
        { key: 'BitrateKbps',              label: 'Бітрейт, кбіт/с',                type: 'number', step: '32' },
        { key: 'ManualDelayMs',            label: 'Ручна затримка, мс',             type: 'number', step: '5' }
    ];

    var overrideItemId = null;

    function openOverride(page, row) {
        overrideItemId = row.ItemId;
        var section = page.querySelector('#anOverrideSection');
        page.querySelector('#anOverrideName').textContent = row.Name;
        section.style.display = '';
        section.scrollIntoView({ behavior: 'smooth', block: 'start' });

        var host = page.querySelector('#anOverrideFields');
        host.innerHTML = 'Завантажую…';

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
                    sp.textContent = f.label;
                    lab.appendChild(cb);
                    lab.appendChild(sp);
                    wrap.appendChild(lab);
                } else {
                    var lbl = document.createElement('label');
                    lbl.textContent = f.label;
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
                            opt.textContent = o[1];
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
            host.textContent = 'Не вдалось завантажити налаштування.';
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
                profile[f.key] = parseInt(el.value, 10);
            } else {
                profile[f.key] = parseFloat(el.value);
            }
        });
        // The title is not exposed per item on purpose: changing it per film would scatter
        // differently named tracks across the library.
        api('POST', 'Override', { ItemId: overrideItemId, Excluded: false, Profile: profile })
            .then(function () {
                Dashboard.alert('Збережено для цього фільму.');
                loadReport(page);
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

    function runSelfTest(page, itemId) {
        var section = page.querySelector('#anSelfTestSection');
        var out = page.querySelector('#anSelfTestOut');
        section.style.display = '';
        out.textContent = 'Перевіряю…';
        section.scrollIntoView({ behavior: 'smooth', block: 'start' });

        api('GET', 'SelfTest/' + itemId + '?run=true').then(function (d) {
            var L = [];
            L.push('Фільм:            ' + (d.ItemName || '—'));
            L.push('Файл:             ' + (d.SourcePath || '—'));
            if (d.Refusal) { L.push(''); L.push('ВІДМОВА: ' + d.Refusal); out.textContent = L.join('\n'); return; }
            L.push('');
            L.push('Аудіодоріжки джерела:');
            (d.AudioStreams || []).forEach(function (s) { L.push('  ' + s); });
            L.push('Обрано:           ' + d.ChosenStream);
            L.push('');
            L.push('Куди ляже файл:   ' + d.OutputPath);
            L.push('Тека доступна:    ' + (d.FolderWritable ? 'так' : 'НІ'));
            L.push('Вільно на диску:  ' + d.FreeSpaceGb + ' ГБ');
            L.push('ffmpeg:           ' + d.FfmpegPath);
            L.push('Сила (m):         ' + d.ResolvedMaxGain);
            if (d.Measured) {
                L.push('');
                L.push('Виміряно: ' + num(d.Measured.IntegratedLufs) + ' LUFS, різниця ' +
                       num(d.Measured.LoudnessRangeLu) + ' LU, пік ' + num(d.Measured.TruePeakDb) + ' dBTP');
            }
            if ((d.Problems || []).length) {
                L.push('');
                L.push('ПРОБЛЕМИ:');
                d.Problems.forEach(function (p) { L.push('  ! ' + p); });
            }
            L.push('');
            L.push('--- команда аналізу (можна вставити в термінал) ---');
            L.push(d.AnalysisCommand);
            L.push('');
            L.push('--- команда кодування ---');
            L.push(d.EncodeCommand);
            if (d.FfmpegOutput) {
                L.push('');
                L.push('--- вивід ffmpeg ---');
                L.push(d.FfmpegOutput);
            }
            out.textContent = L.join('\n');
        }, function (e) {
            out.textContent = 'Запит не вдався: ' + (e && e.statusText ? e.statusText : e);
        });
    }

    document.querySelector('#AudioNormalizerConfigPage').addEventListener('pageshow', function () {
        var page = this;
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

    document.querySelector('#AudioNormalizerConfigPage').addEventListener('click', function (e) {
        var page = this;
        var id = e.target && e.target.closest ? (e.target.closest('button') || {}).id : null;

        if (id === 'anBtnAnalyzeAll') {
            api('POST', 'Analyze', { ItemIds: [] }).then(function (n) {
                Dashboard.alert('Поставлено в чергу на вимірювання: ' + n);
                loadStatus(page);
            });
        } else if (id === 'anBtnGenerateAll') {
            api('POST', 'Generate', { ItemIds: [], Force: false }).then(function (n) {
                Dashboard.alert('Поставлено в чергу на створення: ' + n);
                loadStatus(page);
            });
        } else if (id === 'anBtnCancel') {
            api('POST', 'Cancel').then(function (n) {
                Dashboard.alert('Знято з черги: ' + n);
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
