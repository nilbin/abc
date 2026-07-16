// GENERATED from manifest.baseline.json — do not edit (scripts/generate-types.mjs).
/* eslint-disable */
import type { OperationResponse, ViewResponse, TamClient } from '@tam/core';

export interface Change<T> { original: T | null; value: T | null; }

export interface TypedOperationResponse<TOutput> extends OperationResponse {
  output?: TOutput & Record<string, unknown>;
}

export interface WorkOrdersAssignInput {
  workOrderId: string;
  assigneeActorId: string;
}

export interface WorkOrdersAssignOutput {
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
}

export interface ProjectsCloseInput {
  projectId: string;
}

export interface ProjectsCloseOutput {
  status: "open" | "closed";
}

export interface WorkOrdersCloseInput {
  workOrderId: string;
}

export interface WorkOrdersCloseOutput {
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
}

export interface OrdersCompleteInput {
  orderId: string;
}

export interface OrdersCompleteOutput {
  version: number;
}

export interface WorkOrdersCompleteInput {
  workOrderId: string;
}

export interface WorkOrdersCompleteOutput {
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
}

export interface CustomersCreateInput {
  name: string;
  visitAddress: string;
  email?: string;
  phone?: string;
}

export interface CustomersCreateOutput {
  customerId: string;
}

export interface OrdersCreateInput {
  customerId: string;
  orderType: "service" | "project";
  workAddress: string;
  description: string;
  projectId?: string;
  requestedDate?: string;
  estimatedTotal?: number;
  extensions?: Record<string, Change<unknown>>;
}

export interface OrdersCreateOutput {
  orderId: string;
  number: string;
}

export interface ProjectsCreateInput {
  customerId: string;
  number: string;
  name: string;
  budget?: number;
}

export interface ProjectsCreateOutput {
  projectId: string;
}

export interface StockCreateInput {
  sku: string;
  name: string;
  unit: "piece" | "hour" | "meter" | "kilogram" | "litre";
  unitPrice: number;
}

export interface StockCreateOutput {
  stockItemId: string;
}

export interface WorkOrdersCreateInput {
  projectId: string;
  title: string;
  description: string;
  location: string;
  extensions?: Record<string, Change<unknown>>;
}

export interface WorkOrdersCreateOutput {
  workOrderId: string;
  number: string;
}

export interface StockDeactivateInput {
  stockItemId: string;
}

export interface StockDeactivateOutput {
  isActive: boolean;
}

export interface CustomersEditContactInput {
  customerId: string;
  name?: Change<string>;
  visitAddress?: Change<string>;
  email?: Change<string>;
  phone?: Change<string>;
}

export interface CustomersEditContactOutput {
  customerId: string;
}

export interface OrdersEditDetailsInput {
  orderId: string;
  description?: Change<string>;
  requestedDate?: Change<string>;
  workAddress?: Change<string>;
  estimatedTotal?: Change<number>;
  extensions?: Record<string, Change<unknown>>;
}

export interface OrdersEditDetailsOutput {
  version: number;
}

export interface ProjectsEditDetailsInput {
  projectId: string;
  name?: Change<string>;
  budget?: Change<number>;
}

export interface ProjectsEditDetailsOutput {
  projectId: string;
}

export interface StockEditInput {
  stockItemId: string;
  name?: Change<string>;
  unitPrice?: Change<number>;
}

export interface StockEditOutput {
  stockItemId: string;
}

export interface WorkOrdersEditDetailsInput {
  workOrderId: string;
  title?: Change<string>;
  description?: Change<string>;
  location?: Change<string>;
  extensions?: Record<string, Change<unknown>>;
}

export interface WorkOrdersEditDetailsOutput {
  version: number;
}

export interface ProjectsReopenInput {
  projectId: string;
}

export interface ProjectsReopenOutput {
  status: "open" | "closed";
}

export interface WorkOrdersScheduleInput {
  workOrderId: string;
  scheduledDate: string;
  assigneeActorId: string;
}

export interface WorkOrdersScheduleOutput {
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
}

export interface WorkOrdersStartInput {
  workOrderId: string;
}

export interface WorkOrdersStartOutput {
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
}

export interface ExtensionsDefineFieldInput {
  entity: string;
  key: string;
  type: string;
  labels: Record<string, unknown>;
  required?: boolean;
  maxLength?: number;
  descriptions?: Record<string, unknown>;
  options?: Record<string, unknown>;
}

export interface ExtensionsDefineFieldOutput {
  fieldId: string;
}

export interface ExtensionsRetireFieldInput {
  fieldId: string;
}

export interface ExtensionsRetireFieldOutput {
  fieldId: string;
}

export interface RolesDefineInput {
  name: string;
  permissions?: Record<string, unknown>;
  levels?: Record<string, unknown>;
}

export interface RolesDefineOutput {
  roleId: string;
}

export interface PluginsActivateInput {
  pluginId: string;
}

export interface PluginsActivateOutput {
  pluginId: string;
  active: boolean;
}

export interface PluginsDeactivateInput {
  pluginId: string;
}

export interface PluginsDeactivateOutput {
  pluginId: string;
  active: boolean;
}

export interface PackagesInstallInput {
  document: string;
  dryRun?: boolean;
}

export interface PackagesInstallOutput {
  package: string;
  version: number;
  applied: boolean;
  fieldsAdded: number;
  rolesDefined: number;
}

export interface PackagesUninstallInput {
  package: string;
}

export interface PackagesUninstallOutput {
  package: string;
  fieldsRetired: number;
}

export interface RulesDefineInput {
  name: string;
  onOperation: string;
  condition: string;
  messages: Record<string, unknown>;
  targetField?: string;
}

export interface RulesDefineOutput {
  ruleId: string;
}

export interface RulesRetireInput {
  name: string;
}

export interface RulesRetireOutput {
  name: string;
}

export interface TenantsCreateInput {
  id: string;
  displayName: string;
}

export interface TenantsCreateOutput {
  id: string;
  path: string;
}

export interface TenantsMoveInput {
  tenantId: string;
  newParentId: string;
}

export interface TenantsMoveOutput {
  id: string;
  path: string;
}

export interface TenantsRenameInput {
  tenantId: string;
  displayName: string;
}

export interface TenantsRenameOutput {
  id: string;
  displayName: string;
}

export interface UsersDefineInput {
  userName: string;
  displayName: string;
  password?: string;
  roles: Record<string, unknown>;
}

export interface UsersDefineOutput {
  userId: string;
}

export interface UsersInviteInput {
  email: string;
  displayName: string;
  roles: Record<string, unknown>;
}

export interface UsersInviteOutput {
  userId: string;
  inviteSent: boolean;
}

export interface UsersDeactivateInput {
  userName: string;
}

export interface UsersDeactivateOutput {
  userName: string;
}

export interface SubscriptionsSetPlanInput {
  plan: string;
  seats: number;
  entitlements: Record<string, unknown>;
  status?: string;
  renewsAtIso?: string;
}

export interface SubscriptionsSetPlanOutput {
  plan: string;
  seats: number;
}

export interface SettingsSetInput {
  key: string;
  value: string;
}

export interface SettingsSetOutput {
  key: string;
}

export interface SecretsSetInput {
  key: string;
  value: string;
}

export interface SecretsSetOutput {
  key: string;
}

export interface IntegrationsScheduleInput {
  integrationId: string;
  spec: string;
  enabled?: boolean;
}

export interface IntegrationsScheduleOutput {
  integrationId: string;
  nextRunIso: string;
}

export interface IntegrationsRunInput {
  integrationId: string;
}

export interface IntegrationsRunOutput {
  integrationId: string;
  status: string;
}

export interface IntegrationsRequeueInput {
  id: string;
}

export interface IntegrationsRequeueOutput {
  id: string;
  kind: string;
}

export interface NavOverrideInput {
  nodeId: string;
  hidden?: boolean;
  labels?: Record<string, unknown>;
  order?: number;
  parent?: string;
}

export interface NavOverrideOutput {
  overrideId: string;
}

export interface NavRetireInput {
  nodeId: string;
}

export interface NavRetireOutput {
  nodeId: string;
}

export interface InspectChecklistsCreateInput {
  title: string;
  orderId?: string;
}

export interface InspectChecklistsCreateOutput {
  checklistId: string;
}

export interface InspectChecklistsPassInput {
  checklistId: string;
}

export interface InspectChecklistsPassOutput {
  checklistId: string;
}

export interface ApprovalsApproveInput {
  requestId: string;
}

export interface ApprovalsApproveOutput {
  requestId: string;
  status: string;
}

export interface ApprovalsGroupsAssignInput {
  groupId: string;
  email: string;
}

export interface ApprovalsGroupsAssignOutput {
  groupId: string;
  actorId: string;
}

export interface ApprovalsGroupsDefineInput {
  name: string;
  parentGroupId?: string;
}

export interface ApprovalsGroupsDefineOutput {
  groupId: string;
}

export interface ApprovalsRulesDefineInput {
  operationId: string;
  groupId: string;
  thresholdField?: string;
  threshold?: number;
}

export interface ApprovalsRulesDefineOutput {
  ruleId: string;
}

export interface ApprovalsRejectInput {
  requestId: string;
  note?: string;
}

export interface ApprovalsRejectOutput {
  requestId: string;
  status: string;
}

export interface InvoicingCreateFromOrderInput {
  orderId: string;
}

export interface InvoicingCreateFromOrderOutput {
  invoiceId: string;
}

export interface InvoicingFinalizeInput {
  invoiceId: string;
}

export interface InvoicingFinalizeOutput {
  invoiceId: string;
}

export interface InvoicingMarkPaidInput {
  invoiceId: string;
}

export interface InvoicingMarkPaidOutput {
  invoiceId: string;
}

export interface CustomersDetailRow {
  id: string;
  name: string;
  email?: string;
  phone?: string;
  visitAddress: string;
  isActive: boolean;
}

export interface CustomersDetailQuery {
  customerId?: string;
}

export interface CustomersListRow {
  id: string;
  name: string;
  email?: string;
  phone?: string;
  visitAddress: string;
  isActive: boolean;
}

export interface CustomersListQuery {
  search?: string;
}

export interface CustomersLookupRow {
  id: string;
  name: string;
  isActive: boolean;
}

export interface CustomersLookupQuery {
  search?: string;
}

export interface OrdersDetailRow {
  id: string;
  number: string;
  customerName: string;
  type: "service" | "project";
  status: "open" | "completed" | "cancelled";
  workAddress: string;
  description: string;
  requestedDate?: string;
  estimatedTotal?: number;
  version: number;
  extensions: Record<string, unknown>;
}

export interface OrdersDetailQuery {
  orderId?: string;
}

export interface OrdersListRow {
  id: string;
  number: string;
  customerName: string;
  type: "service" | "project";
  status: "open" | "completed" | "cancelled";
  requestedDate?: string;
  estimatedTotal?: number;
  tenantId: string;
  version: number;
  extensions: Record<string, unknown>;
}

export interface OrdersListQuery {
  search?: string;
}

export interface ProjectsDetailRow {
  id: string;
  number: string;
  name: string;
  customerName: string;
  status: "open" | "closed";
  budget?: number;
}

export interface ProjectsDetailQuery {
  projectId?: string;
}

export interface ProjectsListRow {
  id: string;
  number: string;
  name: string;
  customerName: string;
  status: "open" | "closed";
  budget?: number;
  tenantId: string;
}

export interface ProjectsListQuery {
  search?: string;
}

export interface StockDetailRow {
  id: string;
  sku: string;
  name: string;
  unit: "piece" | "hour" | "meter" | "kilogram" | "litre";
  unitPrice: number;
  isActive: boolean;
}

export interface StockDetailQuery {
  stockItemId?: string;
}

export interface StockListRow {
  id: string;
  sku: string;
  name: string;
  unit: "piece" | "hour" | "meter" | "kilogram" | "litre";
  unitPrice: number;
  isActive: boolean;
}

export interface StockListQuery {
  search?: string;
}

export interface WorkOrdersDetailRow {
  id: string;
  number: string;
  title: string;
  projectNumber: string;
  description: string;
  location: string;
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
  scheduledDate?: string;
  assignedToName?: string;
  version: number;
  extensions: Record<string, unknown>;
}

export interface WorkOrdersDetailQuery {
  workOrderId?: string;
}

export interface WorkOrdersListRow {
  id: string;
  number: string;
  title: string;
  projectNumber: string;
  status: "draft" | "scheduled" | "inProgress" | "done" | "closed";
  scheduledDate?: string;
  assignedToName?: string;
  tenantId: string;
  version: number;
  extensions: Record<string, unknown>;
}

export interface WorkOrdersListQuery {
  search?: string;
}

export interface ExtensionsFieldsRow {
  id: string;
  entity: string;
  key: string;
  type: string;
  required: boolean;
  state: "draft" | "active" | "deprecated" | "retired";
}

export interface ExtensionsFieldsQuery {
  entity?: string;
}

export interface RolesListRow {
  id: string;
  name: string;
  permissions: string;
  levels: string;
}

export interface RolesListQuery {
  search?: string;
}

export interface AuditEntriesRow {
  id: string;
  timestamp: string;
  operationId: string;
  actorName: string;
  entity: string;
  entityId: string;
  field: string;
  oldValue?: string;
  newValue?: string;
}

export interface AuditEntriesQuery {
  entity?: string;
  entityId?: string;
}

export interface PluginsListRow {
  pluginId: string;
  active: boolean;
}

export interface PluginsListQuery {
  search?: string;
}

export interface PackagesListRow {
  id: string;
  package: string;
  version: number;
  installedAt: string;
}

export interface PackagesListQuery {
  search?: string;
}

export interface RulesListRow {
  id: string;
  name: string;
  onOperation: string;
  retired: boolean;
}

export interface RulesListQuery {
  search?: string;
}

export interface TenantsListRow {
  id: string;
  displayName: string;
  parentId?: string;
  path: string;
}

export interface TenantsListQuery {

}

export interface UsersListRow {
  id: string;
  userName: string;
  displayName: string;
  roles: string;
  active: boolean;
}

export interface UsersListQuery {
  search?: string;
}

export interface SubscriptionsCurrentRow {
  plan: string;
  seats: number;
  seatsUsed: number;
  status: string;
  entitlements: string;
  anchorTenantId: string;
}

export interface SubscriptionsCurrentQuery {

}

export interface SettingsListRow {
  key: string;
  value: string;
}

export interface SettingsListQuery {
  search?: string;
}

export interface SecretsListRow {
  key: string;
  isSet: boolean;
}

export interface SecretsListQuery {
  search?: string;
}

export interface IntegrationsRunsRow {
  id: string;
  integrationId: string;
  trigger: string;
  status: string;
  detail?: string;
  ranAt: string;
}

export interface IntegrationsRunsQuery {
  integrationId?: string;
}

export interface IntegrationsDeadLetterRow {
  id: string;
  kind: string;
  integrationId: string;
  reference: string;
  attempts: number;
  lastError?: string;
}

export interface IntegrationsDeadLetterQuery {
  kind?: string;
}

export interface NavOverridesRow {
  id: string;
  nodeId: string;
  hidden: boolean;
  order?: number;
  parent?: string;
  labels: string;
}

export interface NavOverridesQuery {

}

export interface InspectChecklistsListRow {
  id: string;
  title: string;
  passed: boolean;
}

export interface InspectChecklistsListQuery {
  search?: string;
}

export interface ApprovalsGroupsListRow {
  id: string;
  name: string;
  parentGroupId?: string;
}

export interface ApprovalsGroupsListQuery {

}

export interface ApprovalsRequestsListRow {
  id: string;
  operationId: string;
  initiator: string;
  status: string;
  createdAtIso: string;
  outcome?: string;
}

export interface ApprovalsRequestsListQuery {
  status?: string;
}

export interface InvoicingInvoicesDetailRow {
  id: string;
  orderNumber: string;
  status: string;
  amount: number;
  created: string;
}

export interface InvoicingInvoicesDetailQuery {
  invoiceId?: string;
}

export interface InvoicingInvoicesListRow {
  id: string;
  orderNumber: string;
  status: string;
  amount: number;
  created: string;
}

export interface InvoicingInvoicesListQuery {
  orderId?: string;
}

export class TypedTamClient {
  constructor(readonly client: TamClient) {}

  /** work-orders.assign (requires work-orders.assign) */
  workOrdersAssign(input: WorkOrdersAssignInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersAssignOutput>> {
    return this.client.operation("work-orders.assign", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersAssignOutput>>;
  }

  /** projects.close (requires projects.close) */
  projectsClose(input: ProjectsCloseInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ProjectsCloseOutput>> {
    return this.client.operation("projects.close", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ProjectsCloseOutput>>;
  }

  /** work-orders.close (requires work-orders.close) */
  workOrdersClose(input: WorkOrdersCloseInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersCloseOutput>> {
    return this.client.operation("work-orders.close", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersCloseOutput>>;
  }

  /** orders.complete (requires orders.complete) */
  ordersComplete(input: OrdersCompleteInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<OrdersCompleteOutput>> {
    return this.client.operation("orders.complete", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<OrdersCompleteOutput>>;
  }

  /** work-orders.complete (requires work-orders.complete) */
  workOrdersComplete(input: WorkOrdersCompleteInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersCompleteOutput>> {
    return this.client.operation("work-orders.complete", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersCompleteOutput>>;
  }

  /** customers.create (requires customers.create) */
  customersCreate(input: CustomersCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<CustomersCreateOutput>> {
    return this.client.operation("customers.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<CustomersCreateOutput>>;
  }

  /** orders.create (requires orders.create) */
  ordersCreate(input: OrdersCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<OrdersCreateOutput>> {
    return this.client.operation("orders.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<OrdersCreateOutput>>;
  }

  /** projects.create (requires projects.create) */
  projectsCreate(input: ProjectsCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ProjectsCreateOutput>> {
    return this.client.operation("projects.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ProjectsCreateOutput>>;
  }

  /** stock.create (requires stock.manage) */
  stockCreate(input: StockCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<StockCreateOutput>> {
    return this.client.operation("stock.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<StockCreateOutput>>;
  }

  /** work-orders.create (requires work-orders.create) */
  workOrdersCreate(input: WorkOrdersCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersCreateOutput>> {
    return this.client.operation("work-orders.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersCreateOutput>>;
  }

  /** stock.deactivate (requires stock.manage) */
  stockDeactivate(input: StockDeactivateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<StockDeactivateOutput>> {
    return this.client.operation("stock.deactivate", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<StockDeactivateOutput>>;
  }

  /** customers.edit-contact (requires customers.edit) */
  customersEditContact(input: CustomersEditContactInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<CustomersEditContactOutput>> {
    return this.client.operation("customers.edit-contact", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<CustomersEditContactOutput>>;
  }

  /** orders.edit-details (requires orders.edit) */
  ordersEditDetails(input: OrdersEditDetailsInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<OrdersEditDetailsOutput>> {
    return this.client.operation("orders.edit-details", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<OrdersEditDetailsOutput>>;
  }

  /** projects.edit-details (requires projects.edit) */
  projectsEditDetails(input: ProjectsEditDetailsInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ProjectsEditDetailsOutput>> {
    return this.client.operation("projects.edit-details", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ProjectsEditDetailsOutput>>;
  }

  /** stock.edit (requires stock.manage) */
  stockEdit(input: StockEditInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<StockEditOutput>> {
    return this.client.operation("stock.edit", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<StockEditOutput>>;
  }

  /** work-orders.edit-details (requires work-orders.edit) */
  workOrdersEditDetails(input: WorkOrdersEditDetailsInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersEditDetailsOutput>> {
    return this.client.operation("work-orders.edit-details", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersEditDetailsOutput>>;
  }

  /** projects.reopen (requires projects.close) */
  projectsReopen(input: ProjectsReopenInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ProjectsReopenOutput>> {
    return this.client.operation("projects.reopen", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ProjectsReopenOutput>>;
  }

  /** work-orders.schedule (requires work-orders.schedule) */
  workOrdersSchedule(input: WorkOrdersScheduleInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersScheduleOutput>> {
    return this.client.operation("work-orders.schedule", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersScheduleOutput>>;
  }

  /** work-orders.start (requires work-orders.start) */
  workOrdersStart(input: WorkOrdersStartInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<WorkOrdersStartOutput>> {
    return this.client.operation("work-orders.start", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<WorkOrdersStartOutput>>;
  }

  /** extensions.define-field (requires extensions.manage) */
  extensionsDefineField(input: ExtensionsDefineFieldInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ExtensionsDefineFieldOutput>> {
    return this.client.operation("extensions.define-field", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ExtensionsDefineFieldOutput>>;
  }

  /** extensions.retire-field (requires extensions.manage) */
  extensionsRetireField(input: ExtensionsRetireFieldInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ExtensionsRetireFieldOutput>> {
    return this.client.operation("extensions.retire-field", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ExtensionsRetireFieldOutput>>;
  }

  /** roles.define (requires roles.manage) */
  rolesDefine(input: RolesDefineInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<RolesDefineOutput>> {
    return this.client.operation("roles.define", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<RolesDefineOutput>>;
  }

  /** plugins.activate (requires plugins.manage) */
  pluginsActivate(input: PluginsActivateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<PluginsActivateOutput>> {
    return this.client.operation("plugins.activate", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<PluginsActivateOutput>>;
  }

  /** plugins.deactivate (requires plugins.manage) */
  pluginsDeactivate(input: PluginsDeactivateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<PluginsDeactivateOutput>> {
    return this.client.operation("plugins.deactivate", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<PluginsDeactivateOutput>>;
  }

  /** packages.install (requires packages.manage) */
  packagesInstall(input: PackagesInstallInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<PackagesInstallOutput>> {
    return this.client.operation("packages.install", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<PackagesInstallOutput>>;
  }

  /** packages.uninstall (requires packages.manage) */
  packagesUninstall(input: PackagesUninstallInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<PackagesUninstallOutput>> {
    return this.client.operation("packages.uninstall", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<PackagesUninstallOutput>>;
  }

  /** rules.define (requires rules.manage) */
  rulesDefine(input: RulesDefineInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<RulesDefineOutput>> {
    return this.client.operation("rules.define", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<RulesDefineOutput>>;
  }

  /** rules.retire (requires rules.manage) */
  rulesRetire(input: RulesRetireInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<RulesRetireOutput>> {
    return this.client.operation("rules.retire", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<RulesRetireOutput>>;
  }

  /** tenants.create (requires tenants.create) */
  tenantsCreate(input: TenantsCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<TenantsCreateOutput>> {
    return this.client.operation("tenants.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<TenantsCreateOutput>>;
  }

  /** tenants.move (requires tenants.move) */
  tenantsMove(input: TenantsMoveInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<TenantsMoveOutput>> {
    return this.client.operation("tenants.move", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<TenantsMoveOutput>>;
  }

  /** tenants.rename (requires tenants.edit) */
  tenantsRename(input: TenantsRenameInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<TenantsRenameOutput>> {
    return this.client.operation("tenants.rename", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<TenantsRenameOutput>>;
  }

  /** users.define (requires users.manage) */
  usersDefine(input: UsersDefineInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<UsersDefineOutput>> {
    return this.client.operation("users.define", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<UsersDefineOutput>>;
  }

  /** users.invite (requires users.manage) */
  usersInvite(input: UsersInviteInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<UsersInviteOutput>> {
    return this.client.operation("users.invite", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<UsersInviteOutput>>;
  }

  /** users.deactivate (requires users.manage) */
  usersDeactivate(input: UsersDeactivateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<UsersDeactivateOutput>> {
    return this.client.operation("users.deactivate", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<UsersDeactivateOutput>>;
  }

  /** subscriptions.set-plan (requires subscriptions.manage) */
  subscriptionsSetPlan(input: SubscriptionsSetPlanInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<SubscriptionsSetPlanOutput>> {
    return this.client.operation("subscriptions.set-plan", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<SubscriptionsSetPlanOutput>>;
  }

  /** settings.set (requires settings.manage) */
  settingsSet(input: SettingsSetInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<SettingsSetOutput>> {
    return this.client.operation("settings.set", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<SettingsSetOutput>>;
  }

  /** secrets.set (requires secrets.manage) */
  secretsSet(input: SecretsSetInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<SecretsSetOutput>> {
    return this.client.operation("secrets.set", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<SecretsSetOutput>>;
  }

  /** integrations.schedule (requires integrations.manage) */
  integrationsSchedule(input: IntegrationsScheduleInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<IntegrationsScheduleOutput>> {
    return this.client.operation("integrations.schedule", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<IntegrationsScheduleOutput>>;
  }

  /** integrations.run (requires integrations.manage) */
  integrationsRun(input: IntegrationsRunInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<IntegrationsRunOutput>> {
    return this.client.operation("integrations.run", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<IntegrationsRunOutput>>;
  }

  /** integrations.requeue (requires integrations.manage) */
  integrationsRequeue(input: IntegrationsRequeueInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<IntegrationsRequeueOutput>> {
    return this.client.operation("integrations.requeue", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<IntegrationsRequeueOutput>>;
  }

  /** nav.override (requires nav.manage) */
  navOverride(input: NavOverrideInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<NavOverrideOutput>> {
    return this.client.operation("nav.override", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<NavOverrideOutput>>;
  }

  /** nav.retire (requires nav.manage) */
  navRetire(input: NavRetireInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<NavRetireOutput>> {
    return this.client.operation("nav.retire", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<NavRetireOutput>>;
  }

  /** inspect.checklists.create (requires inspect.checklists.manage) */
  inspectChecklistsCreate(input: InspectChecklistsCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<InspectChecklistsCreateOutput>> {
    return this.client.operation("inspect.checklists.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<InspectChecklistsCreateOutput>>;
  }

  /** inspect.checklists.pass (requires inspect.checklists.manage) */
  inspectChecklistsPass(input: InspectChecklistsPassInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<InspectChecklistsPassOutput>> {
    return this.client.operation("inspect.checklists.pass", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<InspectChecklistsPassOutput>>;
  }

  /** approvals.approve (requires approvals.review) */
  approvalsApprove(input: ApprovalsApproveInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ApprovalsApproveOutput>> {
    return this.client.operation("approvals.approve", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ApprovalsApproveOutput>>;
  }

  /** approvals.groups.assign (requires approvals.manage) */
  approvalsGroupsAssign(input: ApprovalsGroupsAssignInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ApprovalsGroupsAssignOutput>> {
    return this.client.operation("approvals.groups.assign", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ApprovalsGroupsAssignOutput>>;
  }

  /** approvals.groups.define (requires approvals.manage) */
  approvalsGroupsDefine(input: ApprovalsGroupsDefineInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ApprovalsGroupsDefineOutput>> {
    return this.client.operation("approvals.groups.define", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ApprovalsGroupsDefineOutput>>;
  }

  /** approvals.rules.define (requires approvals.manage) */
  approvalsRulesDefine(input: ApprovalsRulesDefineInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ApprovalsRulesDefineOutput>> {
    return this.client.operation("approvals.rules.define", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ApprovalsRulesDefineOutput>>;
  }

  /** approvals.reject (requires approvals.review) */
  approvalsReject(input: ApprovalsRejectInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<ApprovalsRejectOutput>> {
    return this.client.operation("approvals.reject", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<ApprovalsRejectOutput>>;
  }

  /** invoicing.create-from-order (requires invoicing.manage) */
  invoicingCreateFromOrder(input: InvoicingCreateFromOrderInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<InvoicingCreateFromOrderOutput>> {
    return this.client.operation("invoicing.create-from-order", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<InvoicingCreateFromOrderOutput>>;
  }

  /** invoicing.finalize (requires invoicing.manage) */
  invoicingFinalize(input: InvoicingFinalizeInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<InvoicingFinalizeOutput>> {
    return this.client.operation("invoicing.finalize", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<InvoicingFinalizeOutput>>;
  }

  /** invoicing.mark-paid (requires invoicing.manage) */
  invoicingMarkPaid(input: InvoicingMarkPaidInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<InvoicingMarkPaidOutput>> {
    return this.client.operation("invoicing.mark-paid", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<InvoicingMarkPaidOutput>>;
  }

  /** view customers.detail (requires customers.read) */
  customersDetail(query?: CustomersDetailQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: CustomersDetailRow[] }> {
    return this.client.view("customers.detail", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: CustomersDetailRow[] }>;
  }

  /** view customers.list (requires customers.read) */
  customersList(query?: CustomersListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: CustomersListRow[] }> {
    return this.client.view("customers.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: CustomersListRow[] }>;
  }

  /** view customers.lookup (requires customers.read) */
  customersLookup(query?: CustomersLookupQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: CustomersLookupRow[] }> {
    return this.client.view("customers.lookup", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: CustomersLookupRow[] }>;
  }

  /** view orders.detail (requires orders.read) */
  ordersDetail(query?: OrdersDetailQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: OrdersDetailRow[] }> {
    return this.client.view("orders.detail", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: OrdersDetailRow[] }>;
  }

  /** view orders.list (requires orders.read) */
  ordersList(query?: OrdersListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: OrdersListRow[] }> {
    return this.client.view("orders.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: OrdersListRow[] }>;
  }

  /** view projects.detail (requires projects.read) */
  projectsDetail(query?: ProjectsDetailQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: ProjectsDetailRow[] }> {
    return this.client.view("projects.detail", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: ProjectsDetailRow[] }>;
  }

  /** view projects.list (requires projects.read) */
  projectsList(query?: ProjectsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: ProjectsListRow[] }> {
    return this.client.view("projects.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: ProjectsListRow[] }>;
  }

  /** view stock.detail (requires stock.read) */
  stockDetail(query?: StockDetailQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: StockDetailRow[] }> {
    return this.client.view("stock.detail", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: StockDetailRow[] }>;
  }

  /** view stock.list (requires stock.read) */
  stockList(query?: StockListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: StockListRow[] }> {
    return this.client.view("stock.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: StockListRow[] }>;
  }

  /** view work-orders.detail (requires work-orders.read) */
  workOrdersDetail(query?: WorkOrdersDetailQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: WorkOrdersDetailRow[] }> {
    return this.client.view("work-orders.detail", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: WorkOrdersDetailRow[] }>;
  }

  /** view work-orders.list (requires work-orders.read) */
  workOrdersList(query?: WorkOrdersListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: WorkOrdersListRow[] }> {
    return this.client.view("work-orders.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: WorkOrdersListRow[] }>;
  }

  /** view extensions.fields (requires extensions.manage) */
  extensionsFields(query?: ExtensionsFieldsQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: ExtensionsFieldsRow[] }> {
    return this.client.view("extensions.fields", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: ExtensionsFieldsRow[] }>;
  }

  /** view roles.list (requires roles.manage) */
  rolesList(query?: RolesListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: RolesListRow[] }> {
    return this.client.view("roles.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: RolesListRow[] }>;
  }

  /** view audit.entries (requires audit.read) */
  auditEntries(query?: AuditEntriesQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: AuditEntriesRow[] }> {
    return this.client.view("audit.entries", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: AuditEntriesRow[] }>;
  }

  /** view plugins.list (requires plugins.manage) */
  pluginsList(query?: PluginsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: PluginsListRow[] }> {
    return this.client.view("plugins.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: PluginsListRow[] }>;
  }

  /** view packages.list (requires packages.manage) */
  packagesList(query?: PackagesListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: PackagesListRow[] }> {
    return this.client.view("packages.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: PackagesListRow[] }>;
  }

  /** view rules.list (requires rules.manage) */
  rulesList(query?: RulesListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: RulesListRow[] }> {
    return this.client.view("rules.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: RulesListRow[] }>;
  }

  /** view tenants.list (requires tenants.read) */
  tenantsList(query?: TenantsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: TenantsListRow[] }> {
    return this.client.view("tenants.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: TenantsListRow[] }>;
  }

  /** view users.list (requires users.manage) */
  usersList(query?: UsersListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: UsersListRow[] }> {
    return this.client.view("users.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: UsersListRow[] }>;
  }

  /** view subscriptions.current (requires subscriptions.read) */
  subscriptionsCurrent(query?: SubscriptionsCurrentQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: SubscriptionsCurrentRow[] }> {
    return this.client.view("subscriptions.current", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: SubscriptionsCurrentRow[] }>;
  }

  /** view settings.list (requires settings.manage) */
  settingsList(query?: SettingsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: SettingsListRow[] }> {
    return this.client.view("settings.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: SettingsListRow[] }>;
  }

  /** view secrets.list (requires secrets.manage) */
  secretsList(query?: SecretsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: SecretsListRow[] }> {
    return this.client.view("secrets.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: SecretsListRow[] }>;
  }

  /** view integrations.runs (requires integrations.manage) */
  integrationsRuns(query?: IntegrationsRunsQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: IntegrationsRunsRow[] }> {
    return this.client.view("integrations.runs", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: IntegrationsRunsRow[] }>;
  }

  /** view integrations.dead-letter (requires integrations.manage) */
  integrationsDeadLetter(query?: IntegrationsDeadLetterQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: IntegrationsDeadLetterRow[] }> {
    return this.client.view("integrations.dead-letter", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: IntegrationsDeadLetterRow[] }>;
  }

  /** view nav.overrides (requires nav.manage) */
  navOverrides(query?: NavOverridesQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: NavOverridesRow[] }> {
    return this.client.view("nav.overrides", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: NavOverridesRow[] }>;
  }

  /** view inspect.checklists.list (requires inspect.checklists.read) */
  inspectChecklistsList(query?: InspectChecklistsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: InspectChecklistsListRow[] }> {
    return this.client.view("inspect.checklists.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: InspectChecklistsListRow[] }>;
  }

  /** view approvals.groups.list (requires approvals.manage) */
  approvalsGroupsList(query?: ApprovalsGroupsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: ApprovalsGroupsListRow[] }> {
    return this.client.view("approvals.groups.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: ApprovalsGroupsListRow[] }>;
  }

  /** view approvals.requests.list (requires approvals.review) */
  approvalsRequestsList(query?: ApprovalsRequestsListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: ApprovalsRequestsListRow[] }> {
    return this.client.view("approvals.requests.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: ApprovalsRequestsListRow[] }>;
  }

  /** view invoicing.invoices.detail (requires invoicing.read) */
  invoicingInvoicesDetail(query?: InvoicingInvoicesDetailQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: InvoicingInvoicesDetailRow[] }> {
    return this.client.view("invoicing.invoices.detail", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: InvoicingInvoicesDetailRow[] }>;
  }

  /** view invoicing.invoices.list (requires invoicing.read) */
  invoicingInvoicesList(query?: InvoicingInvoicesListQuery & { page?: number; pageSize?: number; sort?: string; dir?: 'asc' | 'desc' }): Promise<Omit<ViewResponse, 'rows'> & { rows: InvoicingInvoicesListRow[] }> {
    return this.client.view("invoicing.invoices.list", query as unknown as Record<string, unknown>) as unknown as Promise<Omit<ViewResponse, 'rows'> & { rows: InvoicingInvoicesListRow[] }>;
  }
}
