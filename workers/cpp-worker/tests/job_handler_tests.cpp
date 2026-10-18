#include "job_handler.hpp"

#include <cstdlib>
#include <iostream>
#include <string>

using forge::worker::handle_job;

namespace {

int failures = 0;

void check(bool condition, const std::string& name)
{
    if (condition)
        std::cout << "PASS: " << name << '\n';
    else
    {
        std::cerr << "FAIL: " << name << '\n';
        ++failures;
    }
}

void test_valid_log_analysis()
{
    const auto result = handle_job({
        {"jobType", "log-analysis"},
        {"inputPayload", {
            {"text", "INFO booted\nERROR failed\nWARN retry\n"}
        }},
        {"inputReference", nullptr},
        {"inputRevision", nullptr}
    });

    check(result.succeeded, "valid log-analysis succeeds");
    check(result.result_descriptor.value("analysisVersion", 0) == 1,
          "result has analysis version");
    check(result.result_descriptor.value("totalLines", 0) == 3,
          "result counts lines");
    check(result.result_descriptor.value("errorLines", 0) == 1,
          "result counts ERROR lines");
    check(result.result_descriptor.value("warnLines", 0) == 1,
          "result counts WARN lines");
    check(result.result_descriptor.value("infoLines", 0) == 1,
          "result counts INFO lines");
}

void test_invalid_input_schema()
{
    const auto result = handle_job({
        {"jobType", "log-analysis"},
        {"inputPayload", {
            {"text", "hello"},
            {"unexpected", true}
        }},
        {"inputReference", nullptr}
    });

    check(!result.succeeded, "invalid input is rejected");
    check(result.failure_code == "INVALID_INPUT_SCHEMA",
          "invalid input has a stable failure code");
}

void test_unsupported_artifact()
{
    const auto result = handle_job({
        {"jobType", "log-analysis"},
        {"inputPayload", nullptr},
        {"inputReference", "artifact://logs/server-01"},
        {"inputRevision", "version:v1"}
    });

    check(!result.succeeded, "artifact input is not falsely accepted");
    check(result.failure_code == "UNSUPPORTED_ARTIFACT_REFERENCE",
          "unsupported artifact has a stable failure code");
}

void test_unsupported_job_type()
{
    const auto result = handle_job({
        {"jobType", "unknown-job"},
        {"inputPayload", {{"text", "hello"}}},
        {"inputReference", nullptr}
    });

    check(!result.succeeded, "unsupported job type is rejected");
    check(result.failure_code == "UNSUPPORTED_JOB_TYPE",
          "unsupported type has a stable failure code");
}

}

int main()
{
    test_valid_log_analysis();
    test_invalid_input_schema();
    test_unsupported_artifact();
    test_unsupported_job_type();

    if (failures != 0)
    {
        std::cerr << failures << " test(s) failed.\n";
        return EXIT_FAILURE;
    }

    std::cout << "All job-handler tests passed.\n";
    return EXIT_SUCCESS;
}
