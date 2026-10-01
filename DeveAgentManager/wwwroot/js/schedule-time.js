// Convert at the browser boundary so the pickers use the device's timezone, including DST.
(function (root) {
    const pad = n => String(n).padStart(2, '0');
    const localParts = utc => {
        const d = new Date(utc);
        return { date: `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`,
            time: `${pad(d.getHours())}:${pad(d.getMinutes())}`,
            zone: Intl.DateTimeFormat().resolvedOptions().timeZone };
    };
    const toUtc = (date, time) => {
        if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !/^\d{2}:\d{2}$/.test(time)) return { error: 'Choose a valid date and time.' };
        const [y, m, day] = date.split('-').map(Number), [h, min] = time.split(':').map(Number);
        const d = new Date(y, m - 1, day, h, min);
        const matches = value => {
            const p = localParts(value);
            return p.date === date && p.time === time;
        };
        if (!matches(d)) return { error: 'This local time does not exist, possibly because the clocks change. Choose another time or use a duration.' };
        // Repeated clock times are ambiguous; do not silently choose one DST offset.
        for (let minutes = -180; minutes <= 180; minutes += 30) {
            if (minutes && matches(new Date(d.getTime() + minutes * 60000)))
                return { error: 'This local time occurs twice because the clocks change. Use a duration to select an exact reset.' };
        }
        return { utc: d.toISOString() };
    };
    const describe = utc => {
        const p = localParts(utc);
        return `${p.date} ${p.time} (${p.zone})`;
    };
    root.damSchedule = { localParts, toUtc, describe };
    if (typeof module !== 'undefined') module.exports = root.damSchedule;
})(globalThis);
