export const LOCALES = ['en', 'zh-Hans', 'ja', 'ko', 'es', 'fr', 'de'] as const

type Locale = (typeof LOCALES)[number]

export interface Strings {
  readonly quotaReached: (duration: string | null) => string
  readonly creditsExhausted: string
  readonly validationRequired: string
  readonly verifyAccount: string
  readonly days: (days: number, hours: number) => string
  readonly hours: (hours: number, minutes: number) => string
  readonly minutes: (minutes: number) => string
  readonly soon: string
}

const CATALOG: Record<Locale, Strings> = {
  en: {
    quotaReached: (duration) => `Your Antigravity quota is used up.${duration ? ` It resets in ${duration}.` : ''}`,
    creditsExhausted: 'Your Google One AI credits balance is exhausted.',
    validationRequired: 'Google requires account verification before Antigravity can continue.',
    verifyAccount: 'Verify account',
    days: (days, hours) => `${days}d ${hours}h`,
    hours: (hours, minutes) => `${hours}h ${minutes}m`,
    minutes: (minutes) => `${minutes}m`,
    soon: 'less than a minute'
  },
  'zh-Hans': {
    quotaReached: (duration) => `Antigravity 配额已用完。${duration ? `${duration}后重置。` : ''}`,
    creditsExhausted: 'Google One AI 额度余额已耗尽。',
    validationRequired: 'Google 需要先在浏览器验证账号，Antigravity 才能继续使用。',
    verifyAccount: '前往验证',
    days: (days, hours) => `${days} 天 ${hours} 小时`,
    hours: (hours, minutes) => `${hours} 小时 ${minutes} 分钟`,
    minutes: (minutes) => `${minutes} 分钟`,
    soon: '不到 1 分钟'
  },
  ja: {
    quotaReached: (duration) => `Antigravity のクォータに達しました。${duration ? `${duration}後にリセットされます。` : ''}`,
    creditsExhausted: 'Google One AI クレジットの残高が不足しています。',
    validationRequired: 'Antigravity を続行するには Google アカウントの確認が必要です。',
    verifyAccount: 'アカウントを確認',
    days: (days, hours) => `${days}日${hours}時間`,
    hours: (hours, minutes) => `${hours}時間${minutes}分`,
    minutes: (minutes) => `${minutes}分`,
    soon: '1 分未満'
  },
  ko: {
    quotaReached: (duration) => `Antigravity 할당량을 모두 사용했습니다.${duration ? ` ${duration} 후 초기화됩니다.` : ''}`,
    creditsExhausted: 'Google One AI 크레딧 잔액이 소진되었습니다.',
    validationRequired: 'Antigravity를 계속 사용하려면 Google 계정 인증이 필요합니다.',
    verifyAccount: '계정 인증',
    days: (days, hours) => `${days}일 ${hours}시간`,
    hours: (hours, minutes) => `${hours}시간 ${minutes}분`,
    minutes: (minutes) => `${minutes}분`,
    soon: '1분 미만'
  },
  es: {
    quotaReached: (duration) => `Has agotado tu cuota de Antigravity.${duration ? ` Se restablece en ${duration}.` : ''}`,
    creditsExhausted: 'Tu saldo de créditos de Google One IA se ha agotado.',
    validationRequired: 'Google requiere verificar tu cuenta antes de continuar con Antigravity.',
    verifyAccount: 'Verificar cuenta',
    days: (days, hours) => `${days} d ${hours} h`,
    hours: (hours, minutes) => `${hours} h ${minutes} min`,
    minutes: (minutes) => `${minutes} min`,
    soon: 'menos de un minuto'
  },
  fr: {
    quotaReached: (duration) => `Votre quota Antigravity est épuisé.${duration ? ` Réinitialisation dans ${duration}.` : ''}`,
    creditsExhausted: 'Votre solde de crédits Google One IA est épuisé.',
    validationRequired: 'Google demande la vérification de votre compte avant de continuer.',
    verifyAccount: 'Vérifier le compte',
    days: (days, hours) => `${days} j ${hours} h`,
    hours: (hours, minutes) => `${hours} h ${minutes} min`,
    minutes: (minutes) => `${minutes} min`,
    soon: 'moins d’une minute'
  },
  de: {
    quotaReached: (duration) => `Dein Antigravity-Kontingent ist aufgebraucht.${duration ? ` Zurückgesetzt in ${duration}.` : ''}`,
    creditsExhausted: 'Dein Google One KI-Guthaben ist aufgebraucht.',
    validationRequired: 'Google erfordert eine Kontobestätigung, bevor Antigravity fortfahren kann.',
    verifyAccount: 'Konto bestätigen',
    days: (days, hours) => `${days} T ${hours} Std.`,
    hours: (hours, minutes) => `${hours} Std. ${minutes} Min.`,
    minutes: (minutes) => `${minutes} Min.`,
    soon: 'weniger als einer Minute'
  }
}

export function stringsFor(locale: string): Strings {
  const exact = CATALOG[locale as Locale]
  if (exact !== undefined) return exact
  const base = locale.split('-')[0]
  const match = LOCALES.find((candidate) => candidate.split('-')[0] === base)
  return match !== undefined ? CATALOG[match] : CATALOG.en
}

export function formatDuration(ms: number, strings: Strings): string {
  const minutes = Math.floor(ms / 60_000)
  if (minutes < 1) return strings.soon
  const days = Math.floor(minutes / 1_440)
  const hours = Math.floor((minutes % 1_440) / 60)
  if (days > 0) return strings.days(days, hours)
  if (hours > 0) return strings.hours(hours, minutes % 60)
  return strings.minutes(minutes)
}
