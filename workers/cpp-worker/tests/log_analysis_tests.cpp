#include "log_analysis.hpp"

#include <cstdlib>
#include <iostream>
#include <string>

using forge::worker::analyze_log_text;

namespace {

int failures = 0;

void check(bool condition, const std::string& description)
{
    if (condition)
    {
        std::cout << "PASS: " << description << '\n';
    }
    else
    {
        std::cerr << "FAIL: " << description << '\n';
        ++failures;
    }
}

void test_basic_counts()
{
    const auto result = analyze_log_text(
        "INFO service started\n"
        "ERROR disk failed\n"
        "WARN retrying\n"
        "error ERROR repeated\n"
        "all good");

    check(result.at("totalLines").get<std::size_t>() == 5,
          "counts all logical lines");
    check(result.at("errorLines").get<std::size_t>() == 2,
          "counts ERROR lines case-insensitively");
    check(result.at("warnLines").get<std::size_t>() == 1,
          "counts WARN lines");
    check(result.at("infoLines").get<std::size_t>() == 1,
          "counts INFO lines");
}

void test_multiple_categories_on_one_line()
{
    const auto result = analyze_log_text("INFO WARN ERROR ERROR");

    check(result.at("totalLines").get<std::size_t>() == 1,
          "one input line remains one line");
    check(result.at("errorLines").get<std::size_t>() == 1,
          "repeated ERROR on one line counts once");
    check(result.at("warnLines").get<std::size_t>() == 1,
          "multiple categories can count on one line");
    check(result.at("infoLines").get<std::size_t>() == 1,
          "all present categories are counted");
}

void test_trailing_newline()
{
    const auto result = analyze_log_text("ERROR\nINFO\n");

    check(result.at("totalLines").get<std::size_t>() == 2,
          "trailing newline does not create an extra line");
}

void test_plain_text()
{
    const auto result = analyze_log_text("just a message");

    check(result.at("totalLines").get<std::size_t>() == 1,
          "counts plain text");
    check(result.at("errorLines").get<std::size_t>() == 0 &&
          result.at("warnLines").get<std::size_t>() == 0 &&
          result.at("infoLines").get<std::size_t>() == 0,
          "plain text has zero categorized lines");
}

}

int main()
{
    test_basic_counts();
    test_multiple_categories_on_one_line();
    test_trailing_newline();
    test_plain_text();

    if (failures != 0)
    {
        std::cerr << failures << " test(s) failed.\n";
        return EXIT_FAILURE;
    }

    std::cout << "All log-analysis tests passed.\n";
    return EXIT_SUCCESS;
}
