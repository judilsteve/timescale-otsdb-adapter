
import dayjs from 'dayjs';
import relativeTime from 'dayjs/plugin/relativeTime';
import updateLocale from 'dayjs/plugin/updateLocale';
import utc from 'dayjs/plugin/utc';

dayjs.extend(relativeTime);
dayjs.extend(updateLocale);
dayjs.extend(utc); // Provides .local()

dayjs.updateLocale('en', {
    relativeTime: {
        future: "in %s",
        past: "%s ago",
        s: '%d seconds',
        m: "a minute",
        mm: "%d minutes",
        h: "an hour",
        hh: "%d hours",
        d: "a day",
        dd: "%d days",
        M: "a month",
        MM: "%d months",
        y: "a year",
        yy: "%d years"
    }
});

export function printAbsolute(moment: dayjs.Dayjs) {
    return moment.format('YYYY-MM-DD HH:mm:ss Z');
}

export function printTime(moment: dayjs.Dayjs) {
    return moment.format('HH:mm:ss Z');
}

export function printDate(moment: dayjs.Dayjs) {
    return moment.format('YYYY-MM-DD');
}

export function printRelative(moment: dayjs.Dayjs) {
    return moment.fromNow();
}

export function printForDateTimeLocalInput(moment: dayjs.Dayjs) {
    return moment.local().format('YYYY-MM-DDTHH:mm:ss')
}
