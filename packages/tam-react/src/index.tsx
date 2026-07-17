// @tam/react — generic manifest-driven runtime: OperationForm + ViewGrid + renderer registry,
// with a default renderer pack built on Mantine. Server-defined semantics, client-defined
// presentation (docs/06): nothing here knows about orders or customers.

export { TamProvider, useTam } from './context';
export type { TamContextValue } from './context';
export { registerRenderer, DefaultRenderer, cultureText, keyValueMap } from './renderers';
export type { FieldRenderer, FieldRendererProps } from './renderers';
export { OperationForm } from './OperationForm';
export type { OperationFormProps } from './OperationForm';
export { ViewGrid } from './ViewGrid';
export { registerBadgeColors } from './badges';
export type { ViewGridProps } from './ViewGrid';
export { LookupSelect } from './LookupSelect';
export { NavProvider, useNav, NavModeSwitcher, NavSidebar, NavTabs, NavPage, registerPage } from './nav';
export type { NavState } from './nav';
export type { LookupSelectProps } from './LookupSelect';
export { useTamAuth } from './auth';
export type { TamAuthState, TamAuthStatus } from './auth';
export { ModelPage } from './ModelPage';
export { PluginSlot } from './PluginSlot';
