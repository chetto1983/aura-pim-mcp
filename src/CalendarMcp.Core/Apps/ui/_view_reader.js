  // A fragment, not a script: CalendarView inlines it inside view.html's closure, whose scope
  // it shares with _view_list.js.
  // ---- reader -----------------------------------------------------------

  function facts(pairs) {
    var dl = el("dl", "facts");
    pairs.forEach(function (p) {
      if (!p[1]) return;
      dl.appendChild(el("dt", null, p[0]));
      dl.appendChild(el("dd", null, p[1]));
    });
    return dl.childNodes.length ? dl : null;
  }

  function prose(text) {
    var box = el("div", "prose");
    String(text).split(/\n{2,}/).forEach(function (para) {
      if (para.trim()) box.appendChild(el("p", null, para.trim()));
    });
    return box;
  }

  /**
   * Whether a body is markup. This sniffs the content because not every payload names
   * its format: an event description can be HTML with no field saying so. get_email_details
   * returns text or Markdown, which pass through here unchanged.
   */
  function looksLikeHtml(s) {
    return /<\s*(!doctype|html|head|body|table|div|p|br|span|a|img)[\s>/]/i.test(s);
  }

  /**
   * Read an HTML body as text. DOMParser yields an INERT document -- no script runs
   * and no image, font or stylesheet is fetched, because it has no browsing context --
   * so the markup can be walked for its words without ever touching innerHTML and
   * without the network access this app's CSP forbids outright.
   */
  function htmlToText(html) {
    var doc = new DOMParser().parseFromString(html, "text/html");
    Array.prototype.forEach.call(
      doc.querySelectorAll("script, style, noscript, title"),
      function (n) { n.remove(); });

    var BLOCK = /^(P|DIV|TR|LI|BR|HR|H1|H2|H3|H4|H5|H6|TABLE|SECTION|ARTICLE|HEADER|FOOTER|BLOCKQUOTE|UL|OL)$/;
    var out = [];
    (function walk(node) {
      for (var n = node.firstChild; n; n = n.nextSibling) {
        if (n.nodeType === 3) { out.push(n.nodeValue); continue; }
        if (n.nodeType !== 1) continue;
        var block = BLOCK.test(n.tagName);
        if (block) out.push("\n");
        walk(n);
        if (block) out.push("\n");
      }
    })(doc.body || doc);

    // Mail markup is mostly layout tables, so the walk emits far more blank lines than
    // the message has paragraphs; collapse runs of space and of newline back to prose.
    return out.join("")
      .replace(/[ \t\u00a0]+/g, " ")
      .replace(/ *\n */g, "\n")
      .replace(/\n{3,}/g, "\n\n")
      .trim();
  }

  function bodyText(obj) {
    var body = firstOf(obj, ["bodyText", "textBody", "body", "description", "bodyPreview", "snippet"]);
    return body && looksLikeHtml(body) ? htmlToText(body) : body;
  }

  function chips(labels) {
    var box = el("div", "chips");
    labels.filter(Boolean).forEach(function (t) { box.appendChild(el("span", "chip", t)); });
    return box.childNodes.length ? box : null;
  }

  function whoRow(name, sub) {
    var row = el("div", "who-row");
    row.appendChild(el("div", "monogram", initials(name)));
    var who = el("div");
    who.appendChild(el("b", null, name || "—"));
    if (sub) {
      who.appendChild(document.createElement("br"));
      who.appendChild(el("span", "busy", sub));
    }
    row.appendChild(who);
    return row;
  }

  function backButton() {
    var b = el("button", "back", "‹ Elenco");
    b.type = "button";
    b.addEventListener("click", function () { client.setAttribute("data-reading", "0"); });
    return b;
  }

  function readEvent(it, detail) {
    var e = detail || it.raw;
    var start = parseLocal(e.start_local || e.start_utc);
    var end = parseLocal(e.end_local || e.end_utc);
    reader.appendChild(el("h2", null, e.subject || "(senza titolo)"));
    var when = unknownDate(start) ? "data ignota"
      : dayLabel(start) + (e.isAllDay ? " · tutto il giorno"
          : " · " + hhmm(start) + (end ? "–" + hhmm(end) : ""));
    var f = facts([
      ["quando", when],
      ["dove", e.location],
      ["organizzatore", e.organizer],
      ["calendario", e.calendarId],
      ["account", e.accountId],
      ["stato", e.isCancelled ? "annullato" : (e.status || e.showAs)],
      ["riunione", e.onlineMeetingUrl],
      ["ricorrenza", e.isRecurring ? "ricorrente" : ""]
    ]);
    if (f) reader.appendChild(f);
    // attendeeDetails carries names, attendees only addresses; prefer the richer list.
    var att = (e.attendeeDetails && e.attendeeDetails.length ? e.attendeeDetails : e.attendees || [])
      .map(function (a) {
        return typeof a === "string" ? a : (a.name || a.Name || addressOf(a));
      });
    var c = chips(att);
    if (c) reader.appendChild(c);
    var body = bodyText(e);
    reader.appendChild(body ? prose(body) : el("p", "note", "Nessuna descrizione."));
  }

  function readEmail(it, detail) {
    var m = detail || it.raw;
    var stamp = parseLocal(m.receivedDateTime || m.sentDateTime);
    reader.appendChild(el("h2", null, m.subject || "(senza oggetto)"));
    reader.appendChild(whoRow(m.fromName || m.from, m.fromName ? m.from : ""));
    var f = facts([
      ["ricevuto", unknownDate(stamp) ? "data ignota"
                                      : dayLabel(stamp) + " · " + hhmm(stamp)],
      ["a", (m.toRecipients || m.to || []).map(addressOf).filter(Boolean).join(", ")],
      ["cc", (m.ccRecipients || []).map(addressOf).filter(Boolean).join(", ")],
      ["cartella", m.folder || m.folderName],
      ["account", m.accountId],
      ["stato", m.isRead === false ? "da leggere" : (m.isRead === true ? "letto" : "")]
    ]);
    if (f) reader.appendChild(f);
    var names = (m.attachments || []).map(function (a) {
      return typeof a === "string" ? a : (a.name || a.fileName || a.id);
    });
    var c = chips(names);
    if (c) reader.appendChild(c);
    var body = bodyText(m);
    if (body) reader.appendChild(prose(body));
    else if (detail === undefined) reader.appendChild(el("p", "busy", "Carico il messaggio…"));
    else reader.appendChild(el("p", "note", "Il messaggio non ha corpo di testo."));
  }

  function readContact(it) {
    var c = it.raw;
    reader.appendChild(el("h2", null, it.title));
    reader.appendChild(whoRow(it.title, c.jobTitle || c.companyName || ""));
    var f = facts([
      ["email", (c.emailAddresses || []).map(addressOf).filter(Boolean).join(", ")],
      ["telefono", (c.phoneNumbers || []).map(addressOf).filter(Boolean).join(", ")],
      ["azienda", c.companyName],
      ["ruolo", c.jobTitle],
      ["account", c.accountId],
      ["note", c.notes]
    ]);
    reader.appendChild(f || el("p", "note", "Nessun recapito registrato."));
  }

  function readAccount(it) {
    var a = it.raw;
    reader.appendChild(el("h2", null, it.title));
    var f = facts([
      ["provider", a.provider],
      ["id", a.accountId],
      ["domini", (a.domains || []).join(", ")]
    ]);
    if (f) reader.appendChild(f);
    var caps = chips((a.capabilities || []).map(function (c) {
      return c.name + (c.readOnly ? " (sola lettura)" : "");
    }));
    reader.appendChild(caps || el("p", "note", "Nessuna capability dichiarata."));
  }

  function readCalendar(it) {
    var c = it.raw;
    reader.appendChild(el("h2", null, it.title));
    var f = facts([
      ["id", c.id],
      ["account", c.accountId],
      ["proprietario", c.owner],
      ["predefinito", c.isDefault ? "sì" : ""],
      ["modificabile", c.canEdit === false ? "no" : (c.canEdit ? "sì" : "")]
    ]);
    reader.appendChild(f || el("p", "note", "Nessun dettaglio."));
  }

  var READERS = {
    event: readEvent, email: readEmail, contact: readContact,
    account: readAccount, calendar: readCalendar
  };

  function renderReader() {
    empty(reader);
    if (!state.selected) {
      reader.appendChild(el("p", "note", "Seleziona un elemento per leggerlo."));
      return;
    }
    reader.appendChild(backButton());
    var it = state.selected;
    READERS[it.kind](it, state.detail[keyOf(it)]);
  }

  // ---- detail fetch -----------------------------------------------------

  function keyOf(it) {
    var r = it.raw;
    return it.kind + ":" + (r.eventId || r.id || r.contactId || r.accountId || it.title);
  }

  /**
   * The list tools return a preview; the detail tools return the whole thing.
   * Fetching goes back out through the host as a tools/call rather than a fetch(),
   * which is both what the CSP allows and what lets the host apply its own policy to
   * a call this view makes on the user's behalf.
   */
  function detailRequest(it) {
    var r = it.raw;
    if (it.kind === "email" && r.accountId && r.id) {
      return { tool: "get_email_details", args: { accountId: r.accountId, emailId: r.id } };
    }
    if (it.kind === "event" && r.eventId && r.calendarId) {
      return { tool: "get_calendar_event_details", args: { calendarId: r.calendarId,
               eventId: r.eventId, timeZone: state.timezone || "Europe/Rome" } };
    }
    return null;
  }

  function select(it) {
    state.selected = it;
    client.setAttribute("data-reading", "1");
    draw();

    var k = keyOf(it);
    if (k in state.detail) return; // already fetched, or in flight
    var request = detailRequest(it);
    if (!request) return;

    state.detail[k] = null;
    window.McpApp.callTool(request.tool, request.args).then(function (result) {
      var payload = window.McpApp.payloadOf(result);
      if (payload && payload.emails) payload = payload.emails[0];
      if (payload && payload.events) payload = payload.events[0];
      state.detail[k] = payload || null;
      if (state.selected === it) renderReader();
    }).catch(function (err) {
      state.detail[k] = null;
      if (state.selected !== it) return;
      renderReader();
      reader.appendChild(el("div", "warn",
        "Non sono riuscito a caricare il dettaglio: " + ((err && err.message) || err)));
    });
  }

  // ---- top level --------------------------------------------------------

  function draw() {
    renderRail(state.items);
    renderList(visibleItems(), state.family);
    renderReader();
  }

  function renderRaw(payload, note) {
    client.setAttribute("data-raw", "1");
    empty(reader);
    if (note) reader.appendChild(el("div", "warn", note));
    reader.appendChild(el("pre", "raw",
      typeof payload === "string" ? payload : JSON.stringify(payload, null, 2)));
  }

  function render(payload) {
    client.setAttribute("data-raw", "0");
    if (payload == null) {
      empty(rows);
      rows.appendChild(el("p", "note", "Nessun risultato."));
      return;
    }
    if (typeof payload === "string") {
      try { payload = JSON.parse(payload); } catch (_) { renderRaw(payload); return; }
    }
    try {
      var adapted = adapt(payload);
      if (!adapted) { renderRaw(payload); return; }
      state.items = adapted.items;
      state.family = adapted.family;
      state.source = null;
      state.selected = null;
      state.detail = {};
      state.timezone = payload.timezone || null;
      client.setAttribute("data-reading", "0");
      draw();
      if (payload.warnings) rows.appendChild(el("div", "warn", String(payload.warnings)));
    } catch (err) {
      renderRaw(payload, "La vista non ha potuto rendere questo risultato: " +
        ((err && err.message) || err));
    }
  }

  function onResult(params) {
    var result = params && (params.result || params.toolResult || params);
    render(window.McpApp.payloadOf(result));
  }

  window.McpApp.start({
    name: "calendar-view",
    version: "1.0.0",
    onToolResult: onResult
  }).then(function (init) {
    if (init && init.toolResult) onResult({ result: init.toolResult });
  }).catch(function (err) {
    renderRaw("", "Handshake con l'host non riuscito: " + ((err && err.message) || err));
  });
