// GENERATED from manifest.baseline.json — do not edit (scripts/generate-types.mjs).
/* eslint-disable */
import type { OperationResponse, ViewResponse, TamClient } from '@tam/core';

export interface Change<T> { original: T | null; value: T | null; }

export interface TypedOperationResponse<TOutput> extends OperationResponse {
  output?: TOutput & Record<string, unknown>;
}

export interface OrdersCompleteInput {
  orderId: string;
}

export interface OrdersCompleteOutput {
  version: number;
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
  permissions: Record<string, unknown>;
}

export interface RolesDefineOutput {
  roleId: string;
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
  activeOnly?: boolean;
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
  version: number;
  extensions: Record<string, unknown>;
}

export interface OrdersListQuery {
  status?: "open" | "completed" | "cancelled";
  customerId?: string;
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

export class TypedTamClient {
  constructor(readonly client: TamClient) {}

  /** orders.complete (requires orders.complete) */
  ordersComplete(input: OrdersCompleteInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<OrdersCompleteOutput>> {
    return this.client.operation("orders.complete", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<OrdersCompleteOutput>>;
  }

  /** customers.create (requires customers.create) */
  customersCreate(input: CustomersCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<CustomersCreateOutput>> {
    return this.client.operation("customers.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<CustomersCreateOutput>>;
  }

  /** orders.create (requires orders.create) */
  ordersCreate(input: OrdersCreateInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<OrdersCreateOutput>> {
    return this.client.operation("orders.create", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<OrdersCreateOutput>>;
  }

  /** orders.edit-details (requires orders.edit) */
  ordersEditDetails(input: OrdersEditDetailsInput, options?: { idempotencyKey?: string }): Promise<TypedOperationResponse<OrdersEditDetailsOutput>> {
    return this.client.operation("orders.edit-details", input as unknown as Record<string, unknown>, options) as Promise<TypedOperationResponse<OrdersEditDetailsOutput>>;
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
}
