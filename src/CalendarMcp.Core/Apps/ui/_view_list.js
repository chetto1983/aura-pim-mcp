  // A fragment, not a script: CalendarView inlines it inside view.html's closure, whose scope
  // it shares with _view_reader.js.
  var client = document.getElementById("client");
  var rail = document.getElementById("rail");
  var rows = document.getElementById("rows");
  var reader = document.getElementById("reader");
  var title = document.getElementById("title");
  var scope = document.getElementById("scope");

  var DOW = ["domenica", "lunedì", "martedì", "mercoledì",
             "giovedì", "venerdì", "sabato"];
  var MON = ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno",
             "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"];

  var PROVIDER_SPINE = {
    google: "var(--calendar-google)",
    microsoft: "var(--calendar-microsoft)",
    m365: "var(--calendar-microsoft)",
    outlook: "var(--calendar-microsoft)",
    imap: "var(--calendar-imap)",
    ical: "var(--calendar-ical)",
    json: "var(--calendar-json)"
  };

  var state = { items: [], family: null, source: null, selected: null,
                detail: {}, timezone: null };

  // ---- small helpers ----------------------------------------------------

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) n.textContent = String(text);
    return n;
  }

  function empty(node) { while (node.firstChild) node.removeChild(node.firstChild); }

  /**
   * A local ISO stamp without a zone ("2026-08-24T09:00:00") must not be re-read in
   * the viewer's zone: the server already converted it to the calendar's. So the
   * parts are taken from the string itself, and only a zoned stamp goes through Date.
   */
  function parseLocal(s) {
    if (!s) return null;
    var m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?/.exec(String(s));
    if (!m) return null;
    return { y: +m[1], mo: +m[2] - 1, d: +m[3],
             hh: m[4] == null ? null : +m[4], mm: m[5] == null ? null : +m[5] };
  }

  /** Year 1 is not a date, it is a parse that failed upstream. Say so. */
  function unknownDate(p) { return !p || p.y <= 1; }

  function hhmm(p) {
    if (!p || p.hh == null) return "";
    return (p.hh < 10 ? "0" : "") + p.hh + ":" + (p.mm < 10 ? "0" : "") + p.mm;
  }

  function dayKey(p) { return unknownDate(p) ? "?" : p.y + "-" + p.mo + "-" + p.d; }

  function dayLabel(p) {
    if (unknownDate(p)) return "data ignota";
    var now = new Date();
    if (now.getFullYear() === p.y && now.getMonth() === p.mo && now.getDate() === p.d) {
      return "oggi · " + p.d + " " + MON[p.mo];
    }
    var jsDay = new Date(p.y, p.mo, p.d).getDay();
    return DOW[jsDay] + " " + p.d + " " + MON[p.mo] +
           (p.y === now.getFullYear() ? "" : " " + p.y);
  }

  function spineFor(provider) {
    return PROVIDER_SPINE[String(provider || "").toLowerCase()] || "var(--color-ring-primary, var(--calendar-ring))";
  }

  function initials(name) {
    var parts = String(name || "").trim().split(/\s+/).filter(Boolean);
    if (!parts.length) return "?";
    return (parts[0][0] + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
  }

  /** Providers spell an address half a dozen ways; take whichever one is present. */
  function addressOf(entry) {
    if (!entry) return "";
    if (typeof entry === "string") return entry;
    return entry.Address || entry.address || entry.Number || entry.number ||
           entry.email || entry.Email || entry.emailAddress || "";
  }

  function firstOf(obj, keys) {
    for (var i = 0; i < keys.length; i++) {
      var v = obj && obj[keys[i]];
      if (typeof v === "string" && v.trim()) return v;
    }
    return "";
  }

  // ---- adapting each family to one row shape ----------------------------

  function eventItem(e) {
    var start = parseLocal(e.start_local || e.start_utc);
    var end = parseLocal(e.end_local || e.end_utc);
    var att = (e.attendees || []).length;
    return {
      kind: "event", raw: e, stamp: start,
      source: { key: e.calendarId || e.accountId || "?",
                label: e.calendarId || e.accountId || "calendario",
                provider: e.accountId === "fixture" ? "json" : "google" },
      who: e.organizer || (att ? att + (att === 1 ? " invitato" : " invitati") : ""),
      time: e.isAllDay ? "tutto il giorno"
                       : hhmm(start) + (end ? "–" + hhmm(end) : ""),
      title: e.subject || "(senza titolo)",
      snippet: e.location || e.bodyPreview || "",
      unread: false
    };
  }

  function emailItem(m) {
    var stamp = parseLocal(m.receivedDateTime || m.sentDateTime);
    return {
      kind: "email", raw: m, stamp: stamp,
      source: { key: m.accountId || "?", label: m.accountId || "posta",
                provider: m.provider || "google" },
      who: m.fromName || m.from || "(mittente ignoto)",
      time: unknownDate(stamp) ? "" : hhmm(stamp),
      title: m.subject || "(senza oggetto)",
      snippet: m.bodyPreview || m.snippet || "",
      unread: m.isRead === false
    };
  }

  function contactItem(c) {
    var mail = addressOf((c.emailAddresses || [])[0]);
    var tel = addressOf((c.phoneNumbers || [])[0]);
    var label = c.displayName || [c.givenName, c.surname].filter(Boolean).join(" ") ||
                mail || "(senza nome)";
    return {
      kind: "contact", raw: c, stamp: null,
      source: { key: c.accountId || "?", label: c.accountId || "rubrica",
                provider: c.provider || "google" },
      who: c.companyName || c.jobTitle || "", time: "",
      // Half this address book has no display name, so the title IS the address; repeating
      // it underneath reads as two different facts about one contact.
      title: label,
      snippet: label === mail ? (tel || c.companyName || "") : (mail || tel || ""),
      unread: false
    };
  }

  function accountItem(a) {
    return {
      kind: "account", raw: a, stamp: null,
      source: { key: a.provider || "?", label: a.provider || "account",
                provider: a.provider },
      who: a.provider || "", time: "",
      title: a.displayName || a.accountId || "(account)",
      snippet: (a.capabilities || []).map(function (c) { return c.name; }).join(" · "),
      unread: false
    };
  }

  function calendarItem(c) {
    return {
      kind: "calendar", raw: c, stamp: null,
      source: { key: c.accountId || "?", label: c.accountId || "calendari",
                provider: c.provider },
      who: c.accountId || "", time: c.isDefault ? "predefinito" : "",
      title: c.name || c.displayName || c.id || "(calendario)",
      snippet: c.id || "", unread: false
    };
  }

  var FAMILIES = [
    { key: "events", heading: "Agenda", one: "impegno", many: "impegni", map: eventItem },
    { key: "emails", heading: "Posta", one: "messaggio", many: "messaggi", map: emailItem },
    { key: "contacts", heading: "Contatti", one: "contatto", many: "contatti", map: contactItem },
    { key: "accounts", heading: "Account", one: "account", many: "account", map: accountItem },
    { key: "calendars", heading: "Calendari", one: "calendario", many: "calendari", map: calendarItem }
  ];

  /**
   * Dispatch on the shape, in the order the families are distinguishable. A single
   * event, contact or message arrives unwrapped, so those are recognised by their
   * own marker fields rather than by a container key that will not be there.
   */
  function adapt(payload) {
    for (var i = 0; i < FAMILIES.length; i++) {
      var f = FAMILIES[i];
      if (Array.isArray(payload[f.key])) {
        return { family: f, items: payload[f.key].map(f.map) };
      }
    }
    if (payload.eventId && payload.subject) return adapt({ events: [payload] });
    if (payload.emailAddresses || payload.givenName) return adapt({ contacts: [payload] });
    if (payload.subject && (payload.from || payload.fromName)) return adapt({ emails: [payload] });
    return null;
  }

  // ---- rail -------------------------------------------------------------

  function renderRail(items) {
    empty(rail);
    var order = [], seen = {};
    items.forEach(function (it) {
      var k = it.source.key;
      if (!seen[k]) { seen[k] = { source: it.source, n: 0 }; order.push(k); }
      seen[k].n++;
    });
    // One source is not a choice, it is a label -- and the rail is then just noise.
    // The attribute (not an inline display) so the stylesheet can drop the grid column too.
    client.setAttribute("data-rail", order.length < 2 ? "0" : "1");
    if (order.length < 2) return;
    rail.appendChild(el("h2", null, "Origini"));
    var entries = [{ key: null, label: "Tutte", provider: null, n: items.length }]
      .concat(order.map(function (k) {
        return { key: k, label: seen[k].source.label,
                 provider: seen[k].source.provider, n: seen[k].n };
      }));
    entries.forEach(function (s) {
      var b = el("button", "source");
      b.type = "button";
      b.setAttribute("aria-pressed", state.source === s.key ? "true" : "false");
      var dot = el("span", "dot");
      dot.style.setProperty("--spine", s.provider ? spineFor(s.provider) : "var(--color-text-tertiary, var(--calendar-faint))");
      b.appendChild(dot);
      b.appendChild(el("span", "label", s.label));
      b.appendChild(el("span", "count", s.n));
      b.addEventListener("click", function () {
        state.source = s.key;
        state.selected = null;
        draw();
      });
      rail.appendChild(b);
    });
  }

  // ---- list -------------------------------------------------------------

  function visibleItems() {
    if (!state.source) return state.items;
    return state.items.filter(function (it) { return it.source.key === state.source; });
  }

  function rowNode(it) {
    var b = el("button", "row" + (it.unread ? " unread" : ""));
    b.type = "button";
    b.style.setProperty("--spine", spineFor(it.source.provider));
    b.setAttribute("aria-selected", state.selected === it ? "true" : "false");
    var top = el("div", "top");
    top.appendChild(el("span", "who", it.who || it.source.label));
    if (it.time) top.appendChild(el("span", "time", it.time));
    b.appendChild(top);
    b.appendChild(el("div", "subject", it.title));
    if (it.snippet) b.appendChild(el("div", "snippet", it.snippet));
    b.addEventListener("click", function () { select(it); });
    return b;
  }

  function renderList(items, family) {
    empty(rows);
    title.textContent = family.heading;
    scope.textContent = items.length + " " + (items.length === 1 ? family.one : family.many) +
      (state.timezone ? " · " + state.timezone : "");
    if (!items.length) {
      rows.appendChild(el("p", "note", "Niente da mostrare qui."));
      return;
    }
    var dated = items.some(function (it) { return it.stamp; });
    var lastKey = null;
    items.forEach(function (it) {
      if (dated) {
        var k = dayKey(it.stamp);
        if (k !== lastKey) {
          rows.appendChild(el("div", "daysep", dayLabel(it.stamp)));
          lastKey = k;
        }
      }
      rows.appendChild(rowNode(it));
    });
  }
